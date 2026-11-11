using System.CommandLine;
using Rig.Cli.Devenv;
using Rig.Cli.Forwarding;
using Rig.Core.Devenv;
using Rig.Core.Tests;
using Rig.Core.Tests.Policy;

namespace Rig.Cli.Tests.Devenv;

/// <summary><c>rig devenv claude</c>; ported from <c>test_ai_tools.py</c>. Every home is a temp folder.</summary>
public sealed class DevenvCommandTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    private string Settings => ClaudeBaseline.SettingsPath(_home.Path);

    private string Hook => SessionLedger.HookPath(_home.Path);

    private Command Build(TextWriter output, TextWriter error, string? policy = null) =>
        new DevenvCommand(new DevenvHost(Path.Join(_home.Path, "not-this-home"), policy ?? PolicyFiles.Real, false), output, error).Build();

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args) => await RunWithAsync(null, args);

    private async Task<(int Code, string Out, string Err)> RunWithAsync(string? policy, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Build(output, error, policy)
            .Parse(["claude", "--home", _home.Path, .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, TestContext.Current.CancellationToken);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Devenv_offers_claude_only() =>
        Assert.Equal(["claude"], Build(TextWriter.Null, TextWriter.Null).Subcommands.Select(c => c.Name));

    [Fact]
    public async Task Claude_applies_the_baseline_and_hook_then_reports_up_to_date()
    {
        var (code, output, error) = await RunAsync();

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Contains($"Applying 14 baseline updates to {Settings}:\n  + permissions.defaultMode: 'acceptEdits'\n", output, StringComparison.Ordinal);
        Assert.Contains($"✓ Session-ledger hook script installed at {Hook}\n", output, StringComparison.Ordinal);
        Assert.Contains($"✓ Successfully updated Claude Code settings at {Settings}\n", output, StringComparison.Ordinal);
        Assert.Contains("\nclaude: ", output, StringComparison.Ordinal);
        Assert.True(SessionLedger.ScriptCurrent(Hook));
        Assert.Equal(LedgerHookState.Current, SessionLedger.State(ClaudeBaseline.Load(Settings)!, Hook));
        Assert.False(Directory.Exists(Path.Join(_home.Path, "not-this-home")));

        var (again, second, _) = await RunAsync();

        Assert.Equal(0, again);
        Assert.StartsWith($"✓ Claude Code settings up to date: {Settings}\nclaude: ", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        var (code, output, _) = await RunAsync("--dry-run");

        Assert.Equal(0, code);
        Assert.Contains($"Installing the session-ledger hook script at {Hook}\nDry run: no changes written.\n", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(_home.Path, ".claude")));
    }

    [Fact]
    public async Task Unreadable_settings_are_left_for_the_user_to_fix()
    {
        await File.WriteAllTextAsync(_home.File(".claude/settings.json"), "[1]", TestContext.Current.CancellationToken);

        var (code, output, error) = await RunAsync();

        Assert.Equal(1, code);
        Assert.Empty(output);
        Assert.Equal($"! {Settings} is not a readable JSON object; fix it by hand, then re-run.\n", error);
        Assert.Equal("[1]", await File.ReadAllTextAsync(Settings, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Hook));
    }

    [Fact]
    public async Task A_policy_that_cannot_load_fails_after_the_baseline()
    {
        var (code, output, error) = await RunWithAsync(Path.Join(_home.Path, "none.json"));

        Assert.Equal(1, code);
        Assert.Contains("✓ Successfully updated Claude Code settings", output, StringComparison.Ordinal);
        Assert.StartsWith("rig devenv claude: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_outdated_script_alone_is_reinstalled()
    {
        _ = await RunAsync();
        await File.WriteAllTextAsync(Hook, "// an older script\n", TestContext.Current.CancellationToken);

        var (code, output, _) = await RunAsync();

        Assert.Equal(0, code);
        Assert.StartsWith($"Installing the session-ledger hook script at {Hook}\n✓ Session-ledger hook script installed at {Hook}\nclaude: ", output, StringComparison.Ordinal);
        Assert.True(SessionLedger.ScriptCurrent(Hook));
    }

    [Fact]
    public async Task A_script_that_cannot_install_leaves_the_settings_alone()
    {
        _ = _home.Folder(".claude/hooks/session-ledger.mjs");

        var (code, output, error) = await RunAsync();

        Assert.Equal(1, code);
        Assert.StartsWith($"Failed to install {Hook}: ", error, StringComparison.Ordinal);
        Assert.EndsWith("; Claude Code settings left unchanged.\n", error, StringComparison.Ordinal);
        Assert.DoesNotContain("claude: ", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Settings));
    }

    [Fact]
    public async Task Settings_that_cannot_be_written_fail_before_the_projection()
    {
        _ = _home.Folder($".claude/settings.json.{Environment.ProcessId}.tmp");

        var (code, output, error) = await RunAsync();

        Assert.Equal(1, code);
        Assert.StartsWith("Failed to write Claude settings: ", error, StringComparison.Ordinal);
        Assert.DoesNotContain("claude: ", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Settings));
    }

    [Fact]
    public async Task Check_walks_the_hook_states_and_changes_nothing()
    {
        Assert.Equal((1, "Session-ledger hook: not wired in settings.json\n", string.Empty), await RunAsync("--check"));
        Assert.False(Directory.Exists(Path.Join(_home.Path, ".claude")));

        _ = await RunAsync();
        Assert.Equal((0, "Session-ledger hook: installed\n", string.Empty), await RunAsync("--check"));

        await File.WriteAllTextAsync(Hook, "// an older script\n", TestContext.Current.CancellationToken);
        Assert.Equal((1, "Session-ledger hook: script out of date\n", string.Empty), await RunAsync("--check"));

        File.Delete(Hook);
        Assert.Equal((1, "Session-ledger hook: script missing\n", string.Empty), await RunAsync("--check"));

        await File.WriteAllTextAsync(Settings, "not json", TestContext.Current.CancellationToken);
        Assert.Equal((1, "Session-ledger hook: not wired in settings.json\n", string.Empty), await RunAsync("--check"));
    }

    [Theory]
    [InlineData(new[] { "devenv", "claude" }, false)]
    [InlineData(new[] { "devenv", "claude", "--check" }, false)]
    [InlineData(new[] { "devenv" }, true)]
    [InlineData(new[] { "devenv", "--help" }, true)]
    [InlineData(new[] { "devenv", "setup" }, true)]
    [InlineData(new[] { "devenv", "opencode" }, true)]
    public void Only_the_ported_devenv_subcommands_stay_in_csharp(string[] args, bool forwarded)
    {
        var root = new RootCommand { Build(TextWriter.Null, TextWriter.Null) };

        Assert.Equal(forwarded, Forwarded.Owns(args, root));
    }
}
