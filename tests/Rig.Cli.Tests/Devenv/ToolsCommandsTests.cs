using System.CommandLine;
using Rig.Cli.Devenv;
using Rig.Core.Devenv;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Devenv;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Devenv;

/// <summary><c>rig devenv tools</c>, <c>runtimes</c> and <c>update</c>, over a fake brew or winget. Ported from <c>test_steps.py</c> and <c>test_cli.py</c>.</summary>
public sealed class ToolsCommandsTests : IDisposable
{
    private static readonly Ran Done = new(0, string.Empty, string.Empty);
    private readonly TempDir _home = new();
    private readonly PluginServer _server = new();
    private readonly FontServer _fonts = new();
    private readonly FakeProc _proc = new();
    private readonly HashSet<string> _onPath = new(StringComparer.Ordinal) { "brew" };

    public void Dispose()
    {
        _fonts.Dispose();
        _server.Dispose();
        _home.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Every bundled tool is on the PATH except those named.
    private void Missing(params string[] programs)
    {
        foreach (var tool in ToolCatalog.Parse(ToolCatalog.BundledText, "bundled"))
        {
            if (tool.Install.CheckCmd is { } program && !programs.Contains(program))
            {
                _onPath.Add(program);
            }
        }
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(bool windows, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new DevenvCommand(
            new DevenvHost(_home.Path, "/none.json", windows, "/rig", _home.Path, null, p => _onPath.Contains(p) ? $"/bin/{p}" : null, WorkingDirectory: _home.Path),
            _server.Plugins,
            _fonts.Fonts,
            _proc,
            output,
            error).Build();
        var code = await command.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    private Task<(int Code, string Out, string Err)> RunAsync(params string[] args) => RunAsync(false, args);

    private IEnumerable<IReadOnlyList<string>> Ran => _proc.Requests.Select(r => r.Argv);

    [Fact]
    public async Task Check_lists_every_tool_and_fails_only_when_a_required_one_is_missing()
    {
        Missing("delta");
        _proc.Answer(Done, "brew", "list");

        var (code, output, _) = await RunAsync("tools", "--check");

        Assert.Equal(0, code);
        Assert.StartsWith("Development tools:\n", output, StringComparison.Ordinal);
        Assert.Contains("  Git ", output, StringComparison.Ordinal);
        Assert.Matches(@"\n  git-delta +git +missing +A syntax", output);
        Assert.DoesNotContain(Ran, argv => argv[1] == "install");

        _onPath.Remove("gh");
        var (failed, again, _) = await RunAsync("tools", "--check");

        Assert.Equal(1, failed);
        Assert.Contains("missing (required)", again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_mode_installs_every_missing_tool_and_express_only_the_required()
    {
        Missing("delta", "gh");
        _proc.Answer(new Ran(0, "lazygit\n", string.Empty), "brew", "list").Answer(Done, "brew", "install");

        var (code, output, error) = await RunAsync("tools");

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Contains("Installing GitHub CLI...\n✓ Installed GitHub CLI\n", output, StringComparison.Ordinal);
        Assert.Contains("- T3 Code: no brew package; install it by hand.\n", output, StringComparison.Ordinal);
        Assert.Contains(["brew", "install", "fork"], Ran);
        Assert.Contains(["brew", "install", "git-delta"], Ran);
        Assert.Contains(["brew", "install", "gh"], Ran);
        Assert.DoesNotContain(["brew", "install", "lazygit"], Ran);
        Assert.All(_proc.Requests.Where(r => r.Argv[1] == "install"), r => Assert.False(r.Attached));

        _proc.Requests.Clear();
        _ = await RunAsync("tools", "--mode", "express");

        Assert.Equal([["brew", "install", "gh"]], Ran.Where(argv => argv[1] == "install"));
    }

    [Fact]
    public async Task A_dry_run_names_what_it_would_install_and_installs_nothing()
    {
        Missing("gh");

        var (code, output, _) = await RunAsync("tools", "--dry-run", "--update", "--mode", "express");

        Assert.Equal(0, code);
        Assert.Contains("Dry run: would install 1 tool(s): GitHub CLI.\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Ran, argv => argv[1] is "install" or "upgrade");
    }

    [Fact]
    public async Task Nothing_missing_says_so_and_update_upgrades_attached()
    {
        Missing();
        _proc.Answer(new Ran(0, "fork\n", string.Empty), "brew", "list").Answer(Done, "brew", "upgrade");

        var (code, output, _) = await RunAsync("tools", "--update");

        Assert.Equal(0, code);
        Assert.Contains("✓ All target tools are already installed.\n", output, StringComparison.Ordinal);
        Assert.Equal([["brew", "list"], ["brew", "upgrade"]], Ran);
        Assert.True(_proc.Requests[^1].Attached);
    }

    [Fact]
    public async Task A_failed_install_or_upgrade_exits_1()
    {
        Missing("gh");
        _proc.Answer(new Ran(1, string.Empty, "boom"), "brew");

        var (code, _, error) = await RunAsync("tools", "--update");

        Assert.Equal(1, code);
        Assert.Contains("! Failed to install GitHub CLI via brew.\n", error, StringComparison.Ordinal);
        Assert.Contains("Package upgrade failed: brew exited non-zero.\n", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_package_manager_installs_fail_and_update_says_why()
    {
        Missing("gh");
        _onPath.Remove("brew");

        var (code, _, error) = await RunAsync("tools");
        var (updated, _, why) = await RunAsync(true, "update");

        Assert.Equal(1, code);
        Assert.Contains("! Failed to install GitHub CLI via a package manager (none found).\n", error, StringComparison.Ordinal);
        Assert.Equal(1, updated);
        Assert.Equal("Package upgrade did not run: winget is not on the PATH.\n", why);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Interactive_installs_on_windows_drop_the_silent_flags()
    {
        Missing("gh");
        _onPath.Add("winget");
        _proc.Answer(Done, "winget", "install");

        _ = await RunAsync(true, "tools", "--mode", "agent", "--interactive");

        Assert.Equal(["winget", "install", "--id", "GitHub.cli", "--exact", "--accept-package-agreements", "--accept-source-agreements"], Ran.Single(argv => argv[1] == "install"));
    }

    [Fact]
    public async Task A_misshapen_catalog_is_reported()
    {
        await File.WriteAllTextAsync(_home.File(".devenv/tools/bad.json"), "42", Token);

        var (code, _, error) = await RunAsync("tools", "--check");

        Assert.Equal(1, code);
        Assert.StartsWith("Could not read the tool catalog: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_upgrades_every_package()
    {
        _proc.Answer(Done, "brew", "upgrade");

        var (code, output, _) = await RunAsync("update");

        Assert.Equal(0, code);
        Assert.Equal("Updating development tools...\n✓ Environment update complete.\n", output);
    }

    [Fact]
    public async Task Runtimes_ask_uv_for_the_folders_python_and_fnm_for_the_node_lts()
    {
        Missing();
        await File.WriteAllTextAsync(_home.File(".python-version"), "3.12\n", Token);
        _proc.Answer(Done, "uv").Answer(Done, "fnm");

        var (code, output, _) = await RunAsync("runtimes");

        Assert.Equal(0, code);
        Assert.Equal([["uv", "python", "install", "3.12"], ["fnm", "install", "--lts"]], Ran);
        Assert.Contains("✓ Python 3.12 is available.\n", output, StringComparison.Ordinal);
        Assert.Contains("✓ the Node LTS is available.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtimes_install_a_missing_manager_first_and_report_failures()
    {
        Missing("uv", "fnm");
        _proc.Answer(Done, "brew", "list").Answer(Done, "brew", "install", "uv").Answer(new Ran(1, string.Empty, "nope"), "brew", "install", "fnm")
            .Answer(new Ran(2, string.Empty, "no python\n"), "uv");

        var (code, output, error) = await RunAsync("runtimes");

        Assert.Equal(1, code);
        Assert.Contains("Installing uv...\n", output, StringComparison.Ordinal);
        Assert.Contains("! uv python install 3.13 exited 2: no python\n", error, StringComparison.Ordinal);
        Assert.Contains("! Failed to install Fast Node Manager (fnm).\n", error, StringComparison.Ordinal);
        Assert.DoesNotContain(["fnm", "install", "--lts"], Ran);
    }

    [Fact]
    public async Task A_runtimes_dry_run_runs_nothing()
    {
        Missing("fnm");

        var (code, output, _) = await RunAsync("runtimes", "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("Dry run: would install Fast Node Manager (fnm)\n", output, StringComparison.Ordinal);
        Assert.Contains("Dry run: would ensure the Node LTS with: fnm install --lts\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Ran, argv => argv[0] != "brew" || argv[1] != "list");
    }
}
