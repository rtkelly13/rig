using System.CommandLine;
using System.Text.Json;
using Rig.Core.Devenv;
using Rig.Core.Policy;

namespace Rig.Cli.Devenv;

/// <summary>Where <c>rig devenv</c> sets things up.</summary>
/// <param name="Home">The default home folder.</param>
/// <param name="PolicyPath">The checkout's <c>config/cli-allowlists.json</c>, projected into Claude's permissions.</param>
/// <param name="Windows">Whether settings files are written with CRLF and globs match case-insensitively, and which shim and profile the launcher writes.</param>
/// <param name="Checkout">The rig checkout whose C# CLI the agent launcher runs.</param>
/// <param name="CodeRoot">The default code root, where the agent launcher shim goes.</param>
/// <param name="Shell">The <c>SHELL</c> variable, which picks the POSIX profile.</param>
/// <param name="Find">Finds a program on the PATH, or null.</param>
internal sealed record DevenvHost(string Home, string PolicyPath, bool Windows, string Checkout, string CodeRoot, string? Shell, Func<string, string?> Find);

/// <summary>
/// The ported part of <c>rig devenv</c>: <c>agent-launcher</c>, <c>agents</c>, <c>claude</c>, <c>estate-rules</c>,
/// <c>obsidian-plugins</c> and <c>opencode</c>. Every other <c>devenv</c> subcommand is still Python's, and
/// <see cref="Forwarding.Forwarded.Owns"/> hands those lines over before parsing. Ported from devenv's
/// <c>agent_launcher_setup</c>, <c>agent_shortcut</c>, <c>discovery</c>, <c>claude_code</c>, <c>session_ledger</c>,
/// <c>estate_rules</c>, <c>obsidian_plugins</c> and <c>opencode</c>.
/// </summary>
/// <param name="host">Where it sets up.</param>
/// <param name="plugins">Fetches Obsidian plugins from GitHub.</param>
/// <param name="output">Where progress goes.</param>
/// <param name="error">Where failures go.</param>
internal sealed class DevenvCommand(DevenvHost host, ObsidianPlugins plugins, TextWriter output, TextWriter error)
{
    /// <summary>What <c>rig devenv</c> is, matching the Python group's help.</summary>
    public const string Description = "Unified development environment manager (tools, runtimes, fonts, git, AI coding agents).";

    /// <summary>The <c>devenv</c> group, holding the subcommands C# owns.</summary>
    public Command Build()
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would change without writing it." };
        var home = new Option<string>("--home") { Description = "Home folder to set up (default: yours).", DefaultValueFactory = _ => host.Home };
        var check = new Option<bool>("--check") { Description = "Report whether the session-ledger hook is wired and current, changing nothing; exit 1 when it is not." };
        var claude = new Command("claude", "Merge the Claude Code settings baseline and install the session-ledger SessionEnd hook.") { dryRun, home, check };
        claude.SetAction(parsed => parsed.GetValue(check) ? Check(parsed.GetValue(home)!) : Claude(parsed.GetValue(dryRun), parsed.GetValue(home)!));
        var opencode = new Command("opencode", "Merge OpenCode's default config and project the estate command policy into it.") { dryRun, home };
        opencode.SetAction(parsed => Opencode(parsed.GetValue(dryRun), parsed.GetValue(home)!));
        var rules = new Command("estate-rules", "Install or refresh the estate-wide AGENTS.md under ~/code/personal.") { dryRun, home };
        rules.SetAction(parsed => Rules(parsed.GetValue(dryRun), parsed.GetValue(home)!));
        var vault = new Option<string?>("--vault") { Description = "Vault to set up (default: ~/code/personal/obsidian-vault under --home)." };
        var obsidian = new Command("obsidian-plugins", "Install and enable the estate's Obsidian community and core plugins in a vault.") { dryRun, home, vault };
        obsidian.SetAction((parsed, cancellationToken) =>
            ObsidianAsync(parsed.GetValue(dryRun), parsed.GetValue(vault) ?? ObsidianPlugins.DefaultVault(parsed.GetValue(home)!), cancellationToken));
        var agents = new Command("agents", "List the AI coding agents devenv knows and whether each is on the PATH.");
        agents.SetAction(_ => Agents());
        var root = new Option<string>("--root") { Description = "Code root to write the shim to (default: this estate's).", DefaultValueFactory = _ => host.CodeRoot };
        var profile = new Option<string?>("--profile") { Description = "Shell profile to add the shortcut to (default: the active one under --home)." };
        var launcher = new Command("agent-launcher", "Write the agent launcher shim to the code root and the `agent` shortcut to the shell profile.") { dryRun, home, root, profile };
        launcher.SetAction(parsed => Launcher(parsed.GetValue(dryRun), parsed.GetValue(root)!, parsed.GetValue(profile) ?? DefaultProfile(parsed.GetValue(home)!)));
        return new Command("devenv", Description) { launcher, agents, claude, rules, obsidian, opencode };
    }

    private string DefaultProfile(string home) => host.Windows ? LauncherShim.PowerShellProfile(home) : LauncherShim.PosixProfile(home, host.Shell);

    private int Agents()
    {
        output.WriteLine("Detected AI coding assistants:");
        foreach (var agent in KnownAgents.All)
        {
            output.WriteLine($"  {agent.Name,-26} {agent.Command,-9} {(host.Find(agent.Command) is null ? "not found" : "found")}");
        }

        return 0;
    }

    // The shim at the code root (both shims on Windows), then the profile block calling it. 1 when a write fails.
    private int Launcher(bool dryRun, string root, string profile)
    {
        var cli = LauncherShim.CliProject(LauncherShim.MainCheckout(host.Checkout));
        var sh = Path.Join(root, "agent.sh");
        var shim = host.Windows ? Path.Join(root, "agent.ps1") : sh;
        if (dryRun)
        {
            output.WriteLine($"[dry-run] Would write agent launcher shim to {root}");
        }
        else
        {
            try
            {
                _ = Directory.CreateDirectory(root);
                File.WriteAllText(sh, LauncherShim.Posix(cli));
                if (host.Windows)
                {
                    File.WriteAllText(shim, LauncherShim.PowerShell(cli));
                }
                else if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(sh, (UnixFileMode)0b111_101_101);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Failed to write the agent launcher shim: {e.Message}");
                return 1;
            }

            output.WriteLine($"OK: Agent launcher shim ready: {shim}");
        }

        return Shortcut(dryRun, profile, host.Windows ? LauncherShim.PowerShellBlock(shim) : LauncherShim.PosixBlock(shim));
    }

    private int Shortcut(bool dryRun, string profile, string block)
    {
        try
        {
            var existed = File.Exists(profile);
            // Compared with \n line ends, as Python's text mode does; written with the platform's.
            var existing = existed ? File.ReadAllText(profile).ReplaceLineEndings("\n") : string.Empty;
            var updated = LauncherShim.Upsert(existing, block);
            if (existed && updated == existing)
            {
                output.WriteLine($"Agent shortcut block in {Path.GetFileName(profile)} is already up to date.");
                return 0;
            }

            if (dryRun)
            {
                output.WriteLine($"[dry-run] Would update agent shortcut in {profile}");
                return 0;
            }

            _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(profile))!);
            File.WriteAllText(profile, host.Windows ? updated.ReplaceLineEndings("\r\n") : updated);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Failed to configure agent shortcut: {e.Message}");
            return 1;
        }

        output.WriteLine($"OK: Agent shortcut configured in {Path.GetFileName(profile)}");
        return 0;
    }

    private int Check(string home)
    {
        var settings = SettingsFile.Load(ClaudeBaseline.SettingsPath(home));
        var state = settings is null ? LedgerHookState.NotWired : SessionLedger.State(settings, SessionLedger.HookPath(home));
        output.WriteLine($"Session-ledger hook: {state.Describe()}");
        return state == LedgerHookState.Current ? 0 : 1;
    }

    // Merge the baseline and hook, then project the policy; 1 when the settings are unreadable or a write fails.
    private int Claude(bool dryRun, string home)
    {
        var path = ClaudeBaseline.SettingsPath(home);
        var hook = SessionLedger.HookPath(home);
        if (SettingsFile.Load(path) is not { } existing)
        {
            error.WriteLine($"! {path} is not a readable JSON object; fix it by hand, then re-run.");
            return 1;
        }

        var merge = ClaudeBaseline.Merge(existing, SessionLedger.Command(hook));
        var stale = !SessionLedger.ScriptCurrent(hook);
        if (merge.Changes.Count == 0 && !stale)
        {
            output.WriteLine($"✓ Claude Code settings up to date: {path}");
            return Project("claude", dryRun, home);
        }

        if (merge.Changes.Count > 0)
        {
            output.WriteLine($"Applying {merge.Changes.Count} baseline updates to {path}:");
            foreach (var change in merge.Changes)
            {
                output.WriteLine($"  {change}");
            }
        }

        if (stale)
        {
            output.WriteLine($"Installing the session-ledger hook script at {hook}");
        }

        if (dryRun)
        {
            output.WriteLine("Dry run: no changes written.");
            return Project("claude", dryRun, home);
        }

        // Install the script before wiring it, so the hook never points at nothing.
        if (stale && !Install(hook))
        {
            return 1;
        }

        if (merge.Changes.Count > 0)
        {
            try
            {
                SettingsFile.Save(path, merge.Settings, host.Windows);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"Failed to write Claude settings: {e.Message}");
                return 1;
            }

            output.WriteLine($"✓ Successfully updated Claude Code settings at {path}");
        }

        return Project("claude", dryRun, home);
    }

    private bool Install(string hook)
    {
        try
        {
            SessionLedger.InstallScript(hook);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Failed to install {hook}: {e.Message}; Claude Code settings left unchanged.");
            return false;
        }

        output.WriteLine($"✓ Session-ledger hook script installed at {hook}");
        return true;
    }

    // Merge the OpenCode defaults without clobbering, then project the policy; 1 when the config is unreadable or a write fails.
    private int Opencode(bool dryRun, string home)
    {
        var path = OpencodeDefaults.ConfigPath(home);
        if (SettingsFile.Load(path) is not { } existing)
        {
            error.WriteLine($"! {path} is not a readable JSON object; fix it by hand, then re-run.");
            return 1;
        }

        if (OpencodeDefaults.Merge(existing) is not { } merged)
        {
            output.WriteLine($"✓ OpenCode config already configured: {path}");
        }
        else if (dryRun)
        {
            output.WriteLine($"Dry run: would merge defaults into {path}");
        }
        else
        {
            try
            {
                SettingsFile.Save(path, merged, host.Windows);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"! Failed to write OpenCode config: {e.Message}");
                return 1;
            }

            output.WriteLine($"✓ Configured OpenCode default settings at {path}");
        }

        return Project("opencode", dryRun, home);
    }

    // The estate-wide AGENTS.md: only the marked block is managed. 1 when the write fails.
    private int Rules(bool dryRun, string home)
    {
        var root = EstateRules.Root(home);
        if (!Directory.Exists(root))
        {
            output.WriteLine($"Estate root not present, skipping: {root}");
            return 0;
        }

        var target = Path.Join(root, "AGENTS.md");
        // Read and compared with \n line ends, as Python's text mode does; written with the platform's.
        var merge = EstateRules.Merge(File.Exists(target) ? File.ReadAllText(target).ReplaceLineEndings("\n") : null);
        var action = merge.Action.ToString().ToLowerInvariant();
        if (merge.Action == EstateRulesAction.Unchanged)
        {
            output.WriteLine($"✓ Estate agent rules up to date: {target}");
            return 0;
        }

        output.WriteLine($"{merge.Action} estate agent rules at {target}");
        if (dryRun)
        {
            output.WriteLine("Dry run: no changes written.");
            return 0;
        }

        try
        {
            File.WriteAllText(target, host.Windows ? merge.Content.ReplaceLineEndings("\r\n") : merge.Content);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Failed to write estate rules: {e.Message}");
            return 1;
        }

        output.WriteLine($"✓ Estate agent rules {action} at {target}");
        return 0;
    }

    // Install the default plugins from their latest releases and enable them; 1 when any could not be.
    private async Task<int> ObsidianAsync(bool dryRun, string vault, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Join(vault, ".obsidian")))
        {
            await output.WriteLineAsync($"Not an Obsidian vault, skipping: {vault}").ConfigureAwait(false);
            return 0;
        }

        IReadOnlyList<PluginSpec> registry;
        try
        {
            registry = await plugins.RegistryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (ObsidianPlugins.Fetching(e))
        {
            await error.WriteLineAsync($"Could not fetch the Obsidian plugin registry: {e.Message}").ConfigureAwait(false);
            return 1;
        }

        var failed = false;
        foreach (var id in ObsidianPlugins.DefaultPlugins)
        {
            if (registry.FirstOrDefault(spec => spec.Id == id) is not { } spec)
            {
                await error.WriteLineAsync($"{id}: not in the community plugin registry").ConfigureAwait(false);
                failed = true;
                continue;
            }

            var have = ObsidianPlugins.InstalledVersion(vault, id);
            var install = await plugins.InstallAsync(vault, spec, dryRun, cancellationToken).ConfigureAwait(false);
            if (install.Tag is not { } tag)
            {
                await error.WriteLineAsync(install.Problem).ConfigureAwait(false);
                failed = true;
                continue;
            }

            await output.WriteLineAsync(have is not null && tag.Contains(have, StringComparison.Ordinal)
                ? $"✓ {spec.Name} {have} already installed"
                : $"{(dryRun ? "would install" : "installed")} {spec.Name} {tag}").ConfigureAwait(false);
            if (ObsidianPlugins.Enable(vault, id, dryRun))
            {
                await output.WriteLineAsync($"  + enabled {id}").ConfigureAwait(false);
            }
        }

        foreach (var id in ObsidianPlugins.EnableCore(vault, ObsidianPlugins.DefaultCorePlugins, dryRun))
        {
            await output.WriteLineAsync($"enabled core plugin: {id}").ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            "Restart Obsidian to load newly installed plugins. Plugins needing an in-app API key must be configured in Obsidian's settings.").ConfigureAwait(false);
        return failed ? 1 : 0;
    }

    // The estate policy's command rules, projected into the system's native config as `rig policy sync` does.
    private int Project(string system, bool dryRun, string home)
    {
        try
        {
            var result = NativeProjection.For(system, home, host.Windows).Apply(CommandPolicy.Load(host.PolicyPath, host.Windows), dryRun);
            output.WriteLine($"{result.System}: {result.Status.Name()} — {result.Path ?? "no native path"} ({result.Detail})");
            return result.Status == ProjectionStatus.Failed ? 1 : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            error.WriteLine($"rig devenv {system}: {e.Message}");
            return 1;
        }
    }
}
