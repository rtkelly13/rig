using System.CommandLine;
using Rig.Cli.Devenv;
using Rig.Core.Devenv;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Devenv;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Devenv;

/// <summary><c>rig devenv git</c> and <c>terminal</c>, over a fake git and ssh-keygen. Ported from <c>test_steps.py</c> and <c>test_terminal.py</c>.</summary>
public sealed class GitTerminalCommandsTests : IDisposable
{
    private static readonly Ran Done = new(0, string.Empty, string.Empty);
    private readonly TempDir _home = new();
    private readonly PluginServer _server = new();
    private readonly FontServer _fonts = new();
    private readonly FakeProc _proc = new();
    private readonly HashSet<string> _onPath = new(StringComparer.Ordinal);

    public void Dispose()
    {
        _fonts.Dispose();
        _server.Dispose();
        _home.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(int Code, string Out, string Err)> RunAsync(bool windows, string answers, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var input = new StringReader(answers);
        var command = new DevenvCommand(
            new DevenvHost(_home.Path, "/none.json", windows, "/rig", Path.Join("C:", "code"), "/bin/zsh", p => _onPath.Contains(p) ? $"/bin/{p}" : null, LocalAppData: Path.Join(_home.Path, "local"), Input: input),
            _server.Plugins,
            _fonts.Fonts,
            _proc,
            output,
            error).Build();
        var code = await command.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    private Task<(int Code, string Out, string Err)> RunAsync(params string[] args) => RunAsync(false, string.Empty, args);

    private List<IReadOnlyList<string>> Ran => [.. _proc.Requests.Select(r => r.Argv)];

    private void Identity()
    {
        _ = _proc.Answer(new Ran(0, "Dev\n", string.Empty), "git", "config", "--global", "--get", "user.name");
        _ = _proc.Answer(new Ran(0, "dev@example.com\n", string.Empty), "git", "config", "--global", "--get", "user.email");
    }

    private void Settable() => _ = _proc.Answer(Done, "git", "config", "--global");

    [Fact]
    public async Task Check_reports_the_identity_and_key_and_fails_only_without_an_identity()
    {
        var (missing, output, _) = await RunAsync("git", "--check");

        Assert.Equal(1, missing);
        Assert.Contains("Git identity: incomplete", output, StringComparison.Ordinal);
        Assert.Contains("SSH key:      missing", output, StringComparison.Ordinal);

        Identity();
        var key = _home.File(".ssh/id_ed25519");
        var (ok, again, _) = await RunAsync("git", "--check");

        Assert.Equal(0, ok);
        Assert.Contains("Git identity: Dev <dev@example.com>", again, StringComparison.Ordinal);
        Assert.Contains(key, again, StringComparison.Ordinal);
        Assert.All(Ran, argv => Assert.Contains("--get", argv));
    }

    [Fact]
    public async Task Without_an_identity_express_mode_stops_at_a_warning()
    {
        var (code, output, _) = await RunAsync("git", "--mode", "express");

        Assert.Equal(0, code);
        Assert.Contains("! Git user.name or user.email is not set", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Ran, argv => argv.Contains("init.defaultBranch"));
    }

    [Fact]
    public async Task It_sets_the_defaults_and_routes_the_configured_identities()
    {
        Settable();
        Identity();
        await File.WriteAllTextAsync(_home.File(".config/devenv/config.json"), """{"identities": [{"id": "work", "name": "W", "email": "w@example.com", "match_directory": "~/code/work"}]}""", Token);
        _ = _home.File(".ssh/id_rsa");

        var (code, output, _) = await RunAsync("git", "--mode", "express");

        Assert.Equal(0, code);
        Assert.Contains("✓ Current global Git identity: Dev <dev@example.com>", output, StringComparison.Ordinal);
        Assert.Contains(["git", "config", "--global", "core.autocrlf", "input"], Ran);
        Assert.Contains(["git", "config", "--global", "pull.rebase", "false"], Ran);
        Assert.Equal("[user]\n\tname = W\n\temail = w@example.com\n", await File.ReadAllTextAsync(Path.Join(_home.Path, ".gitconfig-work"), Token));
        Assert.Contains("[includeIf \"gitdir:~/code/work/\"]", await File.ReadAllTextAsync(Path.Join(_home.Path, ".gitconfig"), Token), StringComparison.Ordinal);
        Assert.Contains("✓ SSH key found", output, StringComparison.Ordinal);

        var (again, second, _) = await RunAsync("git");
        Assert.Equal(0, again);
        Assert.DoesNotContain("Configured conditional Git directory routing", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_writes_and_sets_nothing()
    {
        Identity();
        await File.WriteAllTextAsync(_home.File(".config/devenv/config.json"), """{"identities": [{"id": "work", "name": "W", "email": "w@example.com", "match_directory": "~/code/work"}]}""", Token);

        var (code, output, _) = await RunAsync("git", "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("Dry run: would set git config init.defaultBranch main", output, StringComparison.Ordinal);
        Assert.Contains("Dry run: would add the identities' includeIf blocks", output, StringComparison.Ordinal);
        Assert.Contains("Dry run: would offer to generate an ed25519 key", output, StringComparison.Ordinal);
        Assert.All(Ran, argv => Assert.Contains("--get", argv));
        Assert.False(File.Exists(Path.Join(_home.Path, ".gitconfig")));
        Assert.False(File.Exists(Path.Join(_home.Path, ".gitconfig-work")));
    }

    [Fact]
    public async Task Full_mode_asks_for_the_identity_a_folder_identity_and_a_key()
    {
        Settable();
        _ = _proc.Answer(Done, "ssh-keygen");
        const string Answers = "Dev\ndev@example.com\ny\n\n\n\n~/code/mine\n\n";

        var (code, output, _) = await RunAsync(false, Answers, "git");

        Assert.Equal(0, code);
        Assert.Contains(["git", "config", "--global", "user.name", "Dev"], Ran);
        Assert.Contains(["git", "config", "--global", "user.email", "dev@example.com"], Ran);
        Assert.Equal([new GitIdentity("personal", "Dev", "dev@example.com", null, "~/code/mine")], GitSetup.Identities(_home.Path));
        Assert.Contains("[includeIf \"gitdir:~/code/mine/\"]", await File.ReadAllTextAsync(Path.Join(_home.Path, ".gitconfig"), Token), StringComparison.Ordinal);
        var keygen = Assert.Single(Ran, argv => argv[0] == "ssh-keygen");
        Assert.Equal(["ssh-keygen", "-t", "ed25519", "-C", "user@localhost", "-f", Path.Join(_home.Path, ".ssh", "id_ed25519"), "-N", ""], keygen);
        Assert.Contains("✓ Generated new SSH key", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Express_mode_never_prompts_and_a_failed_keygen_fails()
    {
        Settable();
        Identity();

        var (express, _, _) = await RunAsync(false, "y\n", "git", "--mode", "express");
        Assert.Equal(0, express);
        Assert.DoesNotContain(Ran, argv => argv[0] == "ssh-keygen");

        _ = _proc.Answer(new Ran(1, string.Empty, "boom"), "ssh-keygen");
        var (failed, _, error) = await RunAsync(false, "n\ny\n", "git");
        Assert.Equal(1, failed);
        Assert.Contains("! Failed to generate SSH key: boom", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_home_uses_its_own_gitconfig_file()
    {
        var other = _home.Folder("other");
        _ = _proc.Answer(Done, "git", "config", "--file");

        _ = await RunAsync("git", "--check", "--home", other);

        Assert.Contains(["git", "config", "--file", Path.Join(other, ".gitconfig"), "--get", "user.name"], Ran);
        Assert.DoesNotContain(Ran, argv => argv.Contains("--global"));
    }

    [Fact]
    public async Task A_failed_default_fails_the_step()
    {
        Settable();
        Identity();
        _ = _home.File(".ssh/id_rsa");
        _ = _proc.Answer(new Ran(1, string.Empty, "locked"), "git", "config", "--global", "pull.rebase");

        var (code, _, error) = await RunAsync("git");

        Assert.Equal(1, code);
        Assert.Contains("! git config pull.rebase failed: locked", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_adds_the_prompt_hook_once()
    {
        var (absent, skipped, _) = await RunAsync("terminal");
        Assert.Equal(0, absent);
        Assert.Contains("! oh-my-posh is not installed", skipped, StringComparison.Ordinal);

        _onPath.Add("oh-my-posh");
        var (dry, dryOut, _) = await RunAsync("terminal", "--dry-run");
        Assert.Equal(0, dry);
        Assert.Contains("Dry run: would add the prompt hook", dryOut, StringComparison.Ordinal);
        var zshrc = Path.Join(_home.Path, ".zshrc");
        Assert.False(File.Exists(zshrc));

        _ = await RunAsync("terminal");
        var (again, output, _) = await RunAsync("terminal");

        Assert.Equal(0, again);
        Assert.Equal("\n# Oh My Posh Prompt\neval \"$(oh-my-posh init zsh)\"\n", await File.ReadAllTextAsync(zshrc, Token));
        Assert.Contains("✓ Oh My Posh is already configured", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_check_is_quiet_off_windows()
    {
        var (code, output, _) = await RunAsync("terminal", "--check");

        Assert.Equal(0, code);
        Assert.Contains("not used on this platform", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_windows_it_configures_windows_terminal_and_check_reports_it()
    {
        var (none, noneOut, _) = await RunAsync(true, string.Empty, "terminal", "--check");
        Assert.Equal(0, none);
        Assert.Contains("Windows Terminal: not installed", noneOut, StringComparison.Ordinal);

        var settings = _home.File("local/Microsoft/Windows Terminal/settings.json");
        await File.WriteAllTextAsync(settings, "// keep\n{\"snapToGridOnResize\": true,}", Token);
        var (risky, riskyOut, _) = await RunAsync(true, string.Empty, "terminal", "--check");
        Assert.Equal(1, risky);
        Assert.Contains("jitter risk", riskyOut, StringComparison.Ordinal);

        var (dry, _, _) = await RunAsync(true, string.Empty, "terminal", "--dry-run");
        Assert.Equal(0, dry);
        Assert.StartsWith("// keep", await File.ReadAllTextAsync(settings, Token), StringComparison.Ordinal);

        var (code, output, _) = await RunAsync(true, string.Empty, "terminal");
        Assert.Equal(0, code);
        Assert.Contains("✓ Updated Windows Terminal settings", output, StringComparison.Ordinal);
        Assert.Contains($"Set starting directory to {Path.Join("C:", "code")}", output, StringComparison.Ordinal);
        Assert.Equal(new TerminalStatus(settings, false), TerminalSetup.Status(settings));
        var (ok, _, _) = await RunAsync(true, string.Empty, "terminal", "--check");
        Assert.Equal(0, ok);

        var (same, sameOut, _) = await RunAsync(true, string.Empty, "terminal");
        Assert.Equal(0, same);
        Assert.Contains("already configured", sameOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_windows_unreadable_terminal_settings_are_left_alone()
    {
        var settings = _home.File("local/Microsoft/Windows Terminal/settings.json");
        await File.WriteAllTextAsync(settings, "[oops", Token);

        var (code, _, error) = await RunAsync(true, string.Empty, "terminal");

        Assert.Equal(1, code);
        Assert.Contains("is not a JSON object", error, StringComparison.Ordinal);
        Assert.Equal("[oops", await File.ReadAllTextAsync(settings, Token));
    }
}
