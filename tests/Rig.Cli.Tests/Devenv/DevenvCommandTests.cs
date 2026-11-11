using System.CommandLine;
using Rig.Cli.Devenv;
using Rig.Cli.Forwarding;
using Rig.Core.Devenv;
using Rig.Core.Tests;
using Rig.Core.Tests.Devenv;
using Rig.Core.Tests.Policy;

namespace Rig.Cli.Tests.Devenv;

/// <summary><c>rig devenv claude</c>, <c>opencode</c> and <c>estate-rules</c>; ported from <c>test_ai_tools.py</c>. Every home is a temp folder.</summary>
public sealed class DevenvCommandTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly PluginServer _server = new();
    private readonly FontServer _fonts = new();

    public void Dispose()
    {
        _fonts.Dispose();
        _server.Dispose();
        _home.Dispose();
    }

    private string Settings => ClaudeBaseline.SettingsPath(_home.Path);

    private string Hook => SessionLedger.HookPath(_home.Path);

    private Command Build(TextWriter output, TextWriter error, string? policy = null) =>
        new DevenvCommand(
            new DevenvHost(Path.Join(_home.Path, "not-this-home"), policy ?? PolicyFiles.Real, false, Checkout, Path.Join(_home.Path, "not-this-root"), "/bin/zsh", p => p == "claude" ? "/bin/claude" : null),
            _server.Plugins,
            _fonts.Fonts,
            output,
            error).Build();

    private static string Checkout => Path.Join(Path.GetTempPath(), "rig");

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args) => await RunWithAsync(null, args);

    private async Task<(int Code, string Out, string Err)> RunWithAsync(string? policy, params string[] args) =>
        await InvokeAsync(policy, ["claude", .. args]);

    private async Task<(int Code, string Out, string Err)> InvokeAsync(string? policy, string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await Build(output, error, policy)
            .Parse([.. args, "--home", _home.Path])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, TestContext.Current.CancellationToken);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Devenv_offers_the_ported_subcommands() =>
        Assert.Equal(["agent-launcher", "agents", "claude", "estate-rules", "fonts", "gh", "obsidian-plugins", "opencode"], Build(TextWriter.Null, TextWriter.Null).Subcommands.Select(c => c.Name));

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
        Assert.Equal(LedgerHookState.Current, SessionLedger.State(SettingsFile.Load(Settings)!, Hook));
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

    private string Opencode => OpencodeDefaults.ConfigPath(_home.Path);

    private string Rules => Path.Join(_home.Path, "code", "personal", "AGENTS.md");

    [Fact]
    public async Task Opencode_merges_the_defaults_then_reports_them_configured()
    {
        var (code, output, error) = await InvokeAsync(null, ["opencode"]);

        Assert.Equal((0, string.Empty), (code, error));
        Assert.StartsWith($"✓ Configured OpenCode default settings at {Opencode}\nopencode: ", output, StringComparison.Ordinal);
        var written = SettingsFile.Load(Opencode)!;
        Assert.Null(OpencodeDefaults.Merge(written)); // the defaults, plus the policy the projection added
        Assert.True(written.ContainsKey("permission"));

        var (again, second, _) = await InvokeAsync(null, ["opencode"]);

        Assert.Equal(0, again);
        Assert.StartsWith($"✓ OpenCode config already configured: {Opencode}\nopencode: ", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_opencode_dry_run_writes_nothing()
    {
        var (code, output, _) = await InvokeAsync(null, ["opencode", "--dry-run"]);

        Assert.Equal(0, code);
        Assert.StartsWith($"Dry run: would merge defaults into {Opencode}\n", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Opencode));
    }

    [Fact]
    public async Task An_unreadable_opencode_config_is_left_alone()
    {
        await File.WriteAllTextAsync(_home.File(".config/opencode/config.json"), "{\"a\": 1, \"a\": 2}", TestContext.Current.CancellationToken);

        var (code, output, error) = await InvokeAsync(null, ["opencode"]);

        Assert.Equal((1, string.Empty), (code, output));
        Assert.Equal($"! {Opencode} is not a readable JSON object; fix it by hand, then re-run.\n", error);
    }

    [Fact]
    public async Task An_opencode_config_that_cannot_be_written_fails_before_the_projection()
    {
        _ = _home.Folder($".config/opencode/config.json.{Environment.ProcessId}.tmp");

        var (code, output, error) = await InvokeAsync(null, ["opencode"]);

        Assert.Equal(1, code);
        Assert.StartsWith("! Failed to write OpenCode config: ", error, StringComparison.Ordinal);
        Assert.DoesNotContain("opencode: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_opencode_policy_that_cannot_load_fails()
    {
        var (code, _, error) = await InvokeAsync(Path.Join(_home.Path, "none.json"), ["opencode"]);

        Assert.Equal(1, code);
        Assert.StartsWith("rig devenv opencode: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estate_rules_skip_a_home_without_the_estate()
    {
        var (code, output, _) = await InvokeAsync(null, ["estate-rules"]);

        Assert.Equal(0, code);
        Assert.Equal($"Estate root not present, skipping: {Path.Join(_home.Path, "code", "personal")}\n", output);
    }

    [Fact]
    public async Task Estate_rules_are_created_then_up_to_date_and_never_make_a_claude_md()
    {
        _ = _home.Folder("code/personal");

        var (code, output, _) = await InvokeAsync(null, ["estate-rules"]);

        Assert.Equal(0, code);
        Assert.Equal($"Created estate agent rules at {Rules}\n✓ Estate agent rules created at {Rules}\n", output);
        Assert.Equal(EstateRules.Merge(null).Content, await File.ReadAllTextAsync(Rules, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Join(_home.Path, "code", "personal", "CLAUDE.md")));

        Assert.Equal((0, $"✓ Estate agent rules up to date: {Rules}\n", string.Empty), await InvokeAsync(null, ["estate-rules"]));
    }

    [Fact]
    public async Task An_estate_rules_dry_run_writes_nothing()
    {
        await File.WriteAllTextAsync(_home.File("code/personal/AGENTS.md"), "Mine.\n", TestContext.Current.CancellationToken);

        var (code, output, _) = await InvokeAsync(null, ["estate-rules", "--dry-run"]);

        Assert.Equal(0, code);
        Assert.Equal($"Updated estate agent rules at {Rules}\nDry run: no changes written.\n", output);
        Assert.Equal("Mine.\n", await File.ReadAllTextAsync(Rules, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Estate_rules_that_cannot_be_written_fail()
    {
        _ = _home.Folder("code/personal/AGENTS.md");

        var (code, _, error) = await InvokeAsync(null, ["estate-rules"]);

        Assert.Equal(1, code);
        Assert.StartsWith("Failed to write estate rules: ", error, StringComparison.Ordinal);
    }

    private string Vault => ObsidianPlugins.DefaultVault(_home.Path);

    private const string Restart =
        "Restart Obsidian to load newly installed plugins. Plugins needing an in-app API key must be configured in Obsidian's settings.\n";

    [Fact]
    public async Task Obsidian_plugins_skip_a_folder_that_is_not_a_vault()
    {
        Assert.Equal((0, $"Not an Obsidian vault, skipping: {Vault}\n", string.Empty), await InvokeAsync(null, ["obsidian-plugins"]));
        Assert.Empty(_server.Asked);
    }

    [Fact]
    public async Task Obsidian_plugins_install_enable_then_report_installed()
    {
        await File.WriteAllTextAsync(_home.File("code/personal/obsidian-vault/.obsidian/core-plugins.json"), "{}", TestContext.Current.CancellationToken);
        _ = _server.WithPlugins("1.0", [.. ObsidianPlugins.DefaultPlugins]);

        var (code, output, error) = await InvokeAsync(null, ["obsidian-plugins"]);

        Assert.Equal((0, string.Empty), (code, error));
        Assert.Equal(
            string.Concat(ObsidianPlugins.DefaultPlugins.Select(id => $"installed Plugin {id} 1.0\n  + enabled {id}\n")) + "enabled core plugin: properties\n" + Restart,
            output);
        Assert.Equal("1.0", ObsidianPlugins.InstalledVersion(Vault, "templater-obsidian"));

        var (again, second, _) = await InvokeAsync(null, ["obsidian-plugins"]);

        Assert.Equal(0, again);
        Assert.Equal(string.Concat(ObsidianPlugins.DefaultPlugins.Select(id => $"✓ Plugin {id} 1.0 already installed\n")) + Restart, second);
    }

    [Fact]
    public async Task An_obsidian_dry_run_into_another_vault_writes_nothing()
    {
        var vault = _home.Folder("elsewhere/.obsidian");
        _ = _server.WithPlugins("2.0", [.. ObsidianPlugins.DefaultPlugins]);

        var (code, output, _) = await InvokeAsync(null, ["obsidian-plugins", "--dry-run", "--vault", Path.GetDirectoryName(vault)!]);

        Assert.Equal(0, code);
        Assert.StartsWith("would install Plugin obsidian-local-rest-api 2.0\n  + enabled obsidian-local-rest-api\n", output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(vault));
    }

    [Fact]
    public async Task An_unreachable_registry_fails()
    {
        _ = _home.Folder("code/personal/obsidian-vault/.obsidian");

        var (code, output, error) = await InvokeAsync(null, ["obsidian-plugins"]);

        Assert.Equal((1, string.Empty), (code, output));
        Assert.StartsWith("Could not fetch the Obsidian plugin registry: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plugin_that_cannot_install_fails_after_the_rest()
    {
        _ = _home.Folder("code/personal/obsidian-vault/.obsidian");
        _ = _server.WithPlugins("1.0", [.. ObsidianPlugins.DefaultPlugins.Skip(1)]);
        _ = _server.Bodies.Remove(PluginServer.Release("templater-obsidian"));

        var (code, output, error) = await InvokeAsync(null, ["obsidian-plugins"]);

        Assert.Equal(1, code);
        Assert.StartsWith("obsidian-local-rest-api: not in the community plugin registry\ntemplater-obsidian: could not read latest release (", error, StringComparison.Ordinal);
        Assert.Contains("installed Plugin obsidian-tasks-plugin 1.0\n", output, StringComparison.Ordinal);
        Assert.EndsWith(Restart, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agents_lists_each_known_agent_and_whether_it_is_on_the_path()
    {
        using var writer = new StringWriter();
        var code = await Build(writer, TextWriter.Null).Parse(["agents"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var output = writer.ToString().ReplaceLineEndings("\n");

        Assert.Equal(0, code);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Detected AI coding assistants:", lines[0]);
        Assert.Equal(KnownAgents.All.Count + 1, lines.Length);
        Assert.Equal("  Claude Code                claude    found", lines[2]);
        Assert.Equal("  OpenAI Codex               codex     not found", lines[3]);
    }

    private string Root => Path.Join(_home.Path, "code");

    private string Zshrc => Path.Join(_home.Path, ".zshrc");

    [Fact]
    public async Task Agent_launcher_writes_an_executable_shim_and_the_shortcut_then_reports_up_to_date()
    {
        var (code, output, error) = await InvokeAsync(null, ["agent-launcher", "--root", Root]);

        var shim = Path.Join(Root, "agent.sh");
        Assert.Equal((0, string.Empty), (code, error));
        Assert.Equal($"OK: Agent launcher shim ready: {shim}\nOK: Agent shortcut configured in .zshrc\n", output);
        Assert.Equal(LauncherShim.Posix(LauncherShim.CliProject(Checkout)), await File.ReadAllTextAsync(shim, TestContext.Current.CancellationToken));
        Assert.Equal(LauncherShim.PosixBlock(shim) + "\n", await File.ReadAllTextAsync(Zshrc, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Join(Root, "agent.ps1")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(shim));
        }

        var (again, second, _) = await InvokeAsync(null, ["agent-launcher", "--root", Root]);

        Assert.Equal((0, $"OK: Agent launcher shim ready: {shim}\nAgent shortcut block in .zshrc is already up to date.\n"), (again, second));
    }

    [Fact]
    public async Task Agent_launcher_dry_run_writes_nothing()
    {
        var (code, output, _) = await InvokeAsync(null, ["agent-launcher", "--root", Root, "--dry-run"]);

        Assert.Equal((0, $"[dry-run] Would write agent launcher shim to {Root}\n[dry-run] Would update agent shortcut in {Zshrc}\n"), (code, output));
        Assert.False(Directory.Exists(Root));
        Assert.False(File.Exists(Zshrc));
    }

    [Fact]
    public async Task Agent_launcher_reports_a_failed_shim_write()
    {
        await File.WriteAllTextAsync(Root, "a file where the root goes", TestContext.Current.CancellationToken);

        var (code, _, error) = await InvokeAsync(null, ["agent-launcher", "--root", Root]);

        Assert.Equal(1, code);
        Assert.StartsWith("Failed to write the agent launcher shim: ", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Zshrc));
    }

    [Fact]
    public async Task Agent_launcher_reports_a_failed_profile_write()
    {
        var profile = _home.Folder("profile-is-a-folder");

        var (code, _, error) = await InvokeAsync(null, ["agent-launcher", "--root", Root, "--profile", profile]);

        Assert.Equal(1, code);
        Assert.StartsWith("Failed to configure agent shortcut: ", error, StringComparison.Ordinal);
    }

    private string Overlay => GhScopes.Target(_home.Path, null);

    [Fact]
    public async Task Gh_installs_the_overlay_then_reports_it_installed()
    {
        var (missing, report, _) = await InvokeAsync(null, ["gh", "--check"]);

        Assert.Equal(1, missing);
        Assert.Equal($"gh scope overlay ({Overlay}): not installed\n", report);

        var (code, output, error) = await InvokeAsync(null, ["gh"]);

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Equal($"✓ Wrote gh scope overlay (admin:org, read:user) to {Overlay}\n  Check and reconcile the token with: devenv doctor git auth (workspace)\n", output);
        Assert.Equal(GhScopes.Overlay, await File.ReadAllTextAsync(Overlay, TestContext.Current.CancellationToken));

        var (again, second, _) = await InvokeAsync(null, ["gh"]);

        Assert.Equal(0, again);
        Assert.StartsWith("✓ gh scope overlay is installed (admin:org, read:user).\n", second, StringComparison.Ordinal);
        Assert.Equal(0, (await InvokeAsync(null, ["gh", "--check"])).Code);
    }

    [Fact]
    public async Task Gh_dry_run_writes_nothing()
    {
        var (code, output, _) = await InvokeAsync(null, ["gh", "--dry-run"]);

        Assert.Equal(0, code);
        Assert.Equal($"Would write gh scope overlay (admin:org, read:user) to {Overlay}\n", output);
        Assert.False(File.Exists(Overlay));
    }

    [Fact]
    public async Task Gh_reports_a_failed_write()
    {
        _ = _home.File(".config/devenv");

        var (code, _, error) = await InvokeAsync(null, ["gh"]);

        Assert.Equal(1, code);
        Assert.StartsWith("Failed to write the gh scope overlay: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gh_honours_xdg_config_home_only_for_the_default_home()
    {
        var config = Path.Join(_home.Path, "xdg");
        var command = new DevenvCommand(new DevenvHost(_home.Path, PolicyFiles.Real, false, Checkout, _home.Path, null, _ => null, config), _server.Plugins, _fonts.Fonts, TextWriter.Null, TextWriter.Null).Build();

        Assert.Equal(0, await command.Parse(["gh"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(File.Exists(GhScopes.Target(_home.Path, config)));
        Assert.False(File.Exists(Overlay));
    }

    private string Fonts => NerdFonts.Folder(_home.Path, windows: false, macOS: false);

    [Fact]
    public async Task Fonts_installs_each_missing_family_then_reports_them_present()
    {
        _ = _fonts.WithAll();
        var (missing, report, _) = await InvokeAsync(null, ["fonts", "--check"]);

        Assert.Equal(1, missing);
        Assert.Equal("  JetBrainsMono Nerd Font    missing\n  CascadiaCode Nerd Font     missing\n  FiraCode Nerd Font         missing\n", report);

        var (code, output, error) = await InvokeAsync(null, ["fonts"]);

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Equal(
            string.Concat(NerdFonts.All.Select(f => $"Downloading {f.Name} from {NerdFonts.Url(f)}...\n✓ Installed font: {f.Name}\n")),
            output);
        Assert.True(File.Exists(Path.Join(Fonts, "FiraCodeNerdFont-Regular.ttf")));

        var (again, second, _) = await InvokeAsync(null, ["fonts"]);

        Assert.Equal(0, again);
        Assert.Equal(string.Concat(NerdFonts.All.Select(f => $"✓ Font already present: {f.Name}\n")), second);
        Assert.Equal(0, (await InvokeAsync(null, ["fonts", "--check"])).Code);
        Assert.Equal(3, _fonts.Asked.Count);
    }

    [Fact]
    public async Task Fonts_dry_run_downloads_nothing()
    {
        var (code, output, _) = await InvokeAsync(null, ["fonts", "--dry-run"]);

        Assert.Equal(0, code);
        Assert.Equal(string.Concat(NerdFonts.All.Select(f => $"Dry run: would download and install {f.Name}\n")), output);
        Assert.Empty(_fonts.Asked);
    }

    [Fact]
    public async Task Fonts_keeps_going_after_a_failed_family_and_exits_1()
    {
        _ = _fonts.With(NerdFonts.All[1], "CascadiaCodeNerdFont-Regular.ttf");

        var (code, output, error) = await InvokeAsync(null, ["fonts"]);

        Assert.Equal(1, code);
        Assert.Contains("✓ Installed font: CascadiaCode Nerd Font\n", output, StringComparison.Ordinal);
        Assert.StartsWith("! Could not download/install JetBrainsMono Nerd Font: ", error, StringComparison.Ordinal);
        Assert.Contains("! Could not download/install FiraCode Nerd Font: ", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "devenv", "claude" }, false)]
    [InlineData(new[] { "devenv", "claude", "--check" }, false)]
    [InlineData(new[] { "devenv" }, true)]
    [InlineData(new[] { "devenv", "--help" }, true)]
    [InlineData(new[] { "devenv", "setup" }, true)]
    [InlineData(new[] { "devenv", "opencode" }, false)]
    [InlineData(new[] { "devenv", "estate-rules", "--dry-run" }, false)]
    [InlineData(new[] { "devenv", "obsidian-plugins" }, false)]
    [InlineData(new[] { "devenv", "agents" }, false)]
    [InlineData(new[] { "devenv", "agent-launcher" }, false)]
    [InlineData(new[] { "devenv", "gh", "--check" }, false)]
    [InlineData(new[] { "devenv", "fonts", "--check" }, false)]
    [InlineData(new[] { "devenv", "git" }, true)]
    public void Only_the_ported_devenv_subcommands_stay_in_csharp(string[] args, bool forwarded)
    {
        var root = new RootCommand { Build(TextWriter.Null, TextWriter.Null) };

        Assert.Equal(forwarded, Forwarded.Owns(args, root));
    }
}
