using System.CommandLine;
using Rig.Cli.Devenv;
using Rig.Core.Tests;
using Rig.Core.Tests.Devenv;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Devenv;

/// <summary>
/// <c>rig devenv setup</c>, over a root of recording stand-ins for the commands its steps run. Ported from
/// <c>test_runner.py</c>, <c>test_ai_tools.py</c> and the setup handover tests in <c>test_cli.py</c>.
/// </summary>
public sealed class SetupCommandTests : IDisposable
{
    private static readonly string[] Stubbed =
        ["agents", "claude", "opencode", "estate-rules", "obsidian-plugins", "agent-launcher", "fonts", "git", "gh", "terminal", "runtimes", "tools"];

    private readonly TempDir _home = new();
    private readonly List<string> _ran = [];
    private readonly Dictionary<string, int> _exits = new(StringComparer.Ordinal);

    public void Dispose() => _home.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Home => _home.Path;

    private Command Recorder(string name)
    {
        var command = new Command(name) { TreatUnmatchedTokensAsErrors = false };
        command.SetAction(parsed =>
        {
            _ran.Add(string.Join(' ', parsed.Tokens.Select(token => token.Value)));
            return _exits.GetValueOrDefault(name);
        });
        return command;
    }

    private Task<(int Code, string Out, string Err)> RunAsync(string answers, params string[] args) => InvokeAsync(answers, ["devenv", "setup", .. args]);

    private async Task<(int Code, string Out, string Err)> InvokeAsync(string answers, string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var input = new StringReader(answers);
        var host = new DevenvHost(Home, "/none.json", false, "/rig", Path.Join(Home, "code"), "/bin/zsh", _ => null, Input: input);
        var dryRun = new Option<bool>("--dry-run");
        var home = new Option<string>("--home") { DefaultValueFactory = _ => Home };
        var devenv = new Command("devenv");
        foreach (var command in new SetupCommand(host, output, error).Build(dryRun, home))
        {
            devenv.Subcommands.Add(command);
        }

        foreach (var name in Stubbed)
        {
            devenv.Subcommands.Add(Recorder(name));
        }

        var root = new RootCommand { devenv, new Command("browser") { Recorder("profiles") }, new Command("skills") { Recorder("sync") } };
        var code = await root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Agent_mode_runs_every_step_in_order_without_asking()
    {
        var (code, output, _) = await RunAsync(string.Empty, "--mode", "agent", "--dry-run", "--interactive");

        Assert.Equal(0, code);
        var common = $"--dry-run --home {Home}";
        Assert.Equal(
            [
                "devenv agents",
                $"devenv claude {common}",
                $"devenv opencode {common}",
                $"browser profiles {common}",
                $"devenv estate-rules {common}",
                $"devenv obsidian-plugins {common}",
                $"skills sync --system all {common}",
                $"devenv agent-launcher {common}",
                $"devenv fonts {common}",
                $"devenv git {common} --mode agent",
                $"devenv gh {common}",
                $"devenv terminal {common}",
                "devenv runtimes --dry-run --interactive",
                $"devenv tools {common} --mode agent --interactive",
            ],
            _ran);
        Assert.DoesNotContain("[Y/n]", output, StringComparison.Ordinal);
        Assert.Contains("── Step 7/7: Development Tools ──", output, StringComparison.Ordinal);
        Assert.EndsWith("✓ Setup sequence complete!\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Express_mode_takes_each_steps_default_so_terminal_is_skipped()
    {
        var (code, output, _) = await RunAsync(string.Empty, "--mode", "express");

        Assert.Equal(0, code);
        Assert.DoesNotContain(_ran, line => line.StartsWith("devenv terminal", StringComparison.Ordinal));
        Assert.Contains("Skipped: Terminal Customization", output, StringComparison.Ordinal);
        Assert.Contains($"devenv git --home {Home} --mode express", _ran);
        Assert.Contains($"devenv tools --home {Home} --mode express", _ran);
    }

    [Fact]
    public async Task Full_mode_asks_before_each_step_from_the_one_named()
    {
        // From step 5: terminal (default no) is accepted, runtimes declined, tools taken by default.
        var (code, output, _) = await RunAsync("y\nn\n\n", "--step", "5");

        Assert.Equal(0, code);
        Assert.Equal([$"devenv terminal --home {Home}", $"devenv tools --home {Home} --mode full"], _ran);
        Assert.Contains("Would you like to configure terminal customization? [y/N]: ", output, StringComparison.Ordinal);
        Assert.Contains("Skipped: Runtimes", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Step 4/7", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_part_does_not_stop_the_rest_of_its_step_and_agent_mode_carries_on()
    {
        _exits["claude"] = 1;
        _exits["sync"] = 2;
        _exits["fonts"] = 1;

        var (code, output, error) = await RunAsync(string.Empty, "--mode", "agent");

        Assert.Equal(1, code);
        Assert.Equal(14, _ran.Count);
        Assert.Contains("✗ Step 'AI Coding Tools & Skills' failed: rig devenv claude exited 1; rig skills sync exited 2\n", error, StringComparison.Ordinal);
        Assert.Contains("✗ Step 'Font Installation' failed: rig devenv fonts exited 1\n", error, StringComparison.Ordinal);
        Assert.EndsWith("! Setup finished with failed steps.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_mode_stops_after_a_failure_when_told_not_to_continue()
    {
        _exits["fonts"] = 1;

        // Skip AI tools, run fonts, then decline to continue.
        var (code, output, _) = await RunAsync("n\ny\nn\n");

        Assert.Equal(1, code);
        Assert.Equal([$"devenv fonts --home {Home}"], _ran);
        Assert.Contains("Continue with remaining steps? [Y/n]: ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Step 3/7", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_runs_the_first_step_alone_and_reports_each_failure()
    {
        _exits["opencode"] = 1;

        var (code, output, error) = await InvokeAsync(string.Empty, ["devenv", "ai", "--dry-run"]);

        Assert.Equal(1, code);
        Assert.Equal(8, _ran.Count);
        Assert.Equal($"devenv agent-launcher --dry-run --home {Home}", _ran[^1]);
        Assert.DoesNotContain("[Y/n]", output, StringComparison.Ordinal);
        Assert.Equal("✗ rig devenv opencode exited 1\n", error);
    }

    [Fact]
    public void Every_devenv_line_parses_on_the_real_devenv_command()
    {
        using var plugins = new PluginServer();
        using var fonts = new FontServer();
        var devenv = new DevenvCommand(
            new DevenvHost(Home, "/none.json", false, "/rig", Home, "/bin/zsh", _ => null),
            plugins.Plugins,
            fonts.Fonts,
            new FakeProc(),
            TextWriter.Null,
            TextWriter.Null).Build();
        var root = new RootCommand { devenv };
        var run = new SetupRun("express", true, true, Home);

        var lines = SetupCommand.Steps.SelectMany(step => step.Lines(run)).Where(line => line[0] == "devenv").ToList();

        Assert.Equal(12, lines.Count);
        Assert.All(lines, line => Assert.Empty(root.Parse([.. line]).Errors));
    }
}
