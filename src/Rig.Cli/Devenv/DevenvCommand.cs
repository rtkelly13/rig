#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Text.Json;
using Rig.Core.Devenv;
using Rig.Core.Layout;
using Rig.Core.Policy;
using Rig.Core.Processes;

namespace Rig.Cli.Devenv;

/// <summary>Where <c>rig devenv</c> sets things up.</summary>
/// <param name="Home">The default home folder.</param>
/// <param name="PolicyPath">The checkout's <c>config/cli-allowlists.json</c>, projected into Claude's permissions.</param>
/// <param name="Windows">Whether settings files are written with CRLF and globs match case-insensitively, and which shim and profile the launcher writes.</param>
/// <param name="Checkout">The rig checkout whose C# CLI the agent launcher runs.</param>
/// <param name="CodeRoot">The default code root, where the agent launcher shim goes.</param>
/// <param name="Shell">The <c>SHELL</c> variable, which picks the POSIX profile.</param>
/// <param name="Find">Finds a program on the PATH, or null.</param>
/// <param name="ConfigHome">The <c>XDG_CONFIG_HOME</c> variable, which moves the gh scope overlay; ignored under another <c>--home</c>.</param>
/// <param name="MacOS">Whether fonts go to <c>~/Library/Fonts</c>; otherwise Windows' or Linux's user font folder.</param>
/// <param name="WorkingDirectory">Where <c>.devenv/tools</c> catalogs and the Python version are read from (default: <paramref name="Home"/>).</param>
/// <param name="LocalAppData">The <c>LOCALAPPDATA</c> variable, where Windows Terminal's settings live; ignored under another <c>--home</c>.</param>
/// <param name="Input">Where <c>git --interactive</c> reads its answers (default: none, so every prompt takes its default).</param>
/// <param name="Layouts"></param>
internal sealed record DevenvHost(string Home, string PolicyPath, bool Windows, string Checkout, string CodeRoot, string? Shell, Func<string, string?> Find, string? ConfigHome = null, bool MacOS = false, string? WorkingDirectory = null, string? LocalAppData = null, TextReader? Input = null, Func<string, EstateLayout>? Layouts = null)
{
    /// <summary>The estate layout for <paramref name="home"/>, which a command's <c>--home</c> may point elsewhere.</summary>
    public EstateLayout LayoutFor(string home) => (Layouts ?? EstateLayout.Isolated)(home);
}

/// <summary>
/// <c>rig devenv</c>: <see cref="DoctorCommand"/>'s <c>doctor</c>, <see cref="SetupCommand"/>'s <c>setup</c> and <c>ai</c>, <c>agent-launcher</c>, <c>agents</c>, <c>claude</c>, <c>estate-rules</c>,
/// <c>fonts</c>, <c>gh</c>, <c>obsidian-plugins</c>, <c>opencode</c>, <see cref="ToolsCommands"/>' <c>tools</c>, <c>runtimes</c> and <c>update</c>, and <see cref="GitTerminalCommands"/>' <c>git</c> and <c>terminal</c>.
/// </summary>
/// <param name="host">Where it sets up.</param>
/// <param name="plugins">Fetches Obsidian plugins from GitHub.</param>
/// <param name="fonts">Downloads the Nerd Fonts.</param>
/// <param name="proc">Runs the package manager, uv and fnm.</param>
/// <param name="output">Where progress goes.</param>
/// <param name="error">Where failures go.</param>
internal sealed class DevenvCommand(DevenvHost host, ObsidianPlugins plugins, NerdFonts fonts, IProc proc, TextWriter output, TextWriter error)
{
    /// <summary>What <c>rig devenv</c> is, the root help names.</summary>
    public const string Description = "Unified development environment manager (tools, runtimes, fonts, git, AI coding agents).";

    /// <summary>The <c>devenv</c> group, holding the subcommands C# owns.</summary>
    public Command Build()
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would change without writing it." };
        var home = new Option<string>("--home") { Description = "Home folder to set up (default: yours).", DefaultValueFactory = _ => host.Home };
        var check = new Option<bool>("--check") { Description = "Report whether the session-ledger hook is wired and current, changing nothing; exit 1 when it is not." };
        var agy = new Command("agy", "Install or refresh the global rig context block in ~/.gemini/GEMINI.md.") { dryRun, home };
        agy.SetAction(parsed => Agy(parsed.GetValue(dryRun), parsed.GetValue(home)!));
        var claude = new Command("claude", "Merge the Claude Code settings baseline and install the session-ledger SessionEnd hook.") { dryRun, home, check };
        claude.SetAction(parsed => parsed.GetValue(check) ? Check(parsed.GetValue(home)!) : Claude(parsed.GetValue(dryRun), parsed.GetValue(home)!));
        var opencode = new Command("opencode", "Merge OpenCode's default config and project the estate command policy into it.") { dryRun, home };
        opencode.SetAction(parsed => Opencode(parsed.GetValue(dryRun), parsed.GetValue(home)!));
        var rules = new Command("estate-rules", "Install or refresh the estate-wide AGENTS.md in the code root's personal/ folder.") { dryRun, home };
        rules.SetAction(parsed => Rules(parsed.GetValue(dryRun), parsed.GetValue(home)!));
        var vault = new Option<string?>("--vault") { Description = "Vault to set up (default: personal/obsidian-vault in the code root under --home)." };
        var obsidian = new Command("obsidian-plugins", "Install and enable the estate's Obsidian community and core plugins in a vault.") { dryRun, home, vault };
        obsidian.SetAction((parsed, cancellationToken) =>
            ObsidianAsync(parsed.GetValue(dryRun), parsed.GetValue(vault) ?? ObsidianPlugins.DefaultVault(host.LayoutFor(parsed.GetValue(home)!).Personal), cancellationToken));
        var agents = new Command("agents", "List the AI coding agents devenv knows and whether each is on the PATH.");
        agents.SetAction(_ => Agents());
        var root = new Option<string>("--root") { Description = "Code root to write the shim to (default: this estate's).", DefaultValueFactory = _ => host.CodeRoot };
        var profile = new Option<string?>("--profile") { Description = "Shell profile to add the shortcut to (default: the active one under --home)." };
        var launcherCheck = new Option<bool>("--check") { Description = "Report whether the shim and the profile block are current and no retired shim remains, changing nothing; exit 1 when not." };
        var launcher = new Command("agent-launcher", "Write the agent launcher shim to the code root and the `agent` shortcut to the shell profile, removing retired shims.") { dryRun, home, root, profile, launcherCheck };
        launcher.SetAction(parsed => parsed.GetValue(launcherCheck)
            ? LauncherCheck(parsed.GetValue(root)!, parsed.GetValue(profile) ?? DefaultProfile(parsed.GetValue(home)!))
            : Launcher(parsed.GetValue(dryRun), parsed.GetValue(root)!, parsed.GetValue(profile) ?? DefaultProfile(parsed.GetValue(home)!)));
        var ghCheck = new Option<bool>("--check") { Description = "Report whether the gh scope overlay is installed and current, changing nothing; exit 1 when it is not." };
        var gh = new Command("gh", "Install the personal gh token scope overlay that the workspace devenv merges in.") { dryRun, home, ghCheck };
        gh.SetAction(parsed => Gh(parsed.GetValue(dryRun), parsed.GetValue(ghCheck), parsed.GetValue(home)!));
        var fontsCheck = new Option<bool>("--check") { Description = "Report which Nerd Fonts are installed, changing nothing; exit 1 when one is missing." };
        var fontsCommand = new Command("fonts", "Download and install the developer Nerd Fonts (JetBrainsMono, CascadiaCode, FiraCode).") { dryRun, home, fontsCheck };
        fontsCommand.SetAction((parsed, cancellationToken) =>
            FontsAsync(parsed.GetValue(dryRun), parsed.GetValue(fontsCheck), NerdFonts.Folder(parsed.GetValue(home)!, host.Windows, host.MacOS), cancellationToken));
        var group = new Command("devenv", Description) { new DoctorCommand(host, proc, TimeProvider.System, output).Build(), launcher, agents, agy, claude, rules, fontsCommand, gh, obsidian, opencode };
        foreach (var command in new SetupCommand(host, output, error).Build(dryRun, home).Concat(new ToolsCommands(host, proc, output, error).Build(dryRun, home)).Concat(new GitTerminalCommands(host, proc, output, error).Build(dryRun, home)))
        {
            group.Subcommands.Add(command);
        }

        return group;
    }

    // Each missing family is downloaded in turn; one that fails does not stop the rest, and makes the exit 1.
    private async Task<int> FontsAsync(bool dryRun, bool check, string folder, CancellationToken cancellationToken)
    {
        if (check)
        {
            foreach (var font in NerdFonts.All)
            {
                await output.WriteLineAsync($"  {font.Name,-26} {(NerdFonts.Installed(folder, font) ? "installed" : "missing")}", cancellationToken).ConfigureAwait(false);
            }

            return NerdFonts.All.All(font => NerdFonts.Installed(folder, font)) ? 0 : 1;
        }

        var failed = false;
        foreach (var font in NerdFonts.All)
        {
            if (NerdFonts.Installed(folder, font))
            {
                await output.WriteLineAsync($"✓ Font already present: {font.Name}", cancellationToken).ConfigureAwait(false);
            }
            else if (dryRun)
            {
                await output.WriteLineAsync($"Dry run: would download and install {font.Name}", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync($"Downloading {font.Name} from {NerdFonts.Url(font)}...", cancellationToken).ConfigureAwait(false);
                if (await fonts.InstallAsync(font, folder, cancellationToken).ConfigureAwait(false) is { } problem)
                {
                    await error.WriteLineAsync($"! Could not download/install {font.Name}: {problem}", cancellationToken).ConfigureAwait(false);
                    failed = true;
                }
                else
                {
                    await output.WriteLineAsync($"✓ Installed font: {font.Name}", cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return failed ? 1 : 0;
    }

    // The overlay goes where the workspace devenv reads it. XDG_CONFIG_HOME applies only to the default home, so a
    // temporary --home never writes into the real config folder.
    private int Gh(bool dryRun, bool check, string home)
    {
        var target = GhScopes.Target(home, home == host.Home ? host.ConfigHome : null);
        var scopes = string.Join(", ", GhScopes.Required);
        var state = GhScopes.State(target);
        if (check)
        {
            output.WriteLine($"gh scope overlay ({target}): {state.Describe()}");
            return state == OverlayState.InSync ? 0 : 1;
        }

        if (state == OverlayState.InSync)
        {
            output.WriteLine($"✓ gh scope overlay is installed ({scopes}).");
        }
        else if (dryRun)
        {
            output.WriteLine($"Would write gh scope overlay ({scopes}) to {target}");
            return 0;
        }
        else
        {
            if (!Attempt("Failed to write the gh scope overlay", () => Put(target, GhScopes.Overlay)))
            {
                return 1;
            }

            output.WriteLine($"✓ Wrote gh scope overlay ({scopes}) to {target}");
        }

        output.WriteLine("  Check and reconcile the token with: devenv doctor git auth (workspace)");
        return 0;
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

    private string LauncherCli => LauncherShim.CliProject(LauncherShim.MainCheckout(host.Checkout));

    private string LauncherShimPath(string root) => Path.Join(root, host.Windows ? "agent.ps1" : "agent.sh");

    private string LauncherBlock(string shim) => host.Windows ? LauncherShim.PowerShellBlock(shim) : LauncherShim.PosixBlock(shim);

    // The shim at the code root (both shims on Windows), then the profile block calling it, then the retired shims go.
    // 1 when a write fails.
    private int Launcher(bool dryRun, string root, string profile)
    {
        var shim = LauncherShimPath(root);
        if (dryRun)
        {
            output.WriteLine($"[dry-run] Would write agent launcher shim to {root}");
        }
        else
        {
            if (!Attempt("Failed to write the agent launcher shim", () => WriteShims(root, LauncherCli)))
            {
                return 1;
            }

            output.WriteLine($"OK: Agent launcher shim ready: {shim}");
        }

        var shortcut = Shortcut(dryRun, profile, LauncherBlock(shim));
        return RemoveRetired(dryRun, root) | shortcut;
    }

    // A retired shim devenv wrote is deleted; a file of the same name it did not write stays.
    private int RemoveRetired(bool dryRun, string root)
    {
        var failed = false;
        foreach (var path in Directory.Exists(root) ? LauncherShim.RetiredIn(root, host.Windows) : [])
        {
            if (dryRun)
            {
                output.WriteLine($"[dry-run] Would remove retired shim {path}");
            }
            else if (Attempt("Failed to remove a retired shim", () => File.Delete(path)))
            {
                output.WriteLine($"OK: Removed retired shim {path}");
            }
            else
            {
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    // What agent-launcher would change, reported line by line; 1 when it would change anything.
    private int LauncherCheck(string root, string profile)
    {
        var shim = LauncherShimPath(root);
        var expected = host.Windows ? LauncherShim.PowerShell(LauncherCli) : LauncherShim.Posix(LauncherCli);
        var shimCurrent = File.Exists(shim) && File.ReadAllText(shim) == expected;
        var blockCurrent = LauncherShim.Current(ReadLf(profile), LauncherBlock(shim));
        var retired = Directory.Exists(root) ? LauncherShim.RetiredIn(root, host.Windows) : [];
        output.WriteLine($"  {"Agent launcher shim",-20} {(shimCurrent ? "✓ Current" : "! Missing or stale")}   {shim}");
        output.WriteLine($"  {"Agent shortcut",-20} {(blockCurrent ? "✓ Current" : "! Missing or stale")}   {profile}");
        foreach (var path in retired)
        {
            output.WriteLine($"  {"Retired shim",-20} ! Still present   {path}");
        }

        return shimCurrent && blockCurrent && retired.Count == 0 ? 0 : 1;
    }

    private void WriteShims(string root, string cli)
    {
        var sh = Path.Join(root, "agent.sh");
        Put(sh, LauncherShim.Posix(cli));
        if (host.Windows)
        {
            File.WriteAllText(Path.Join(root, "agent.ps1"), LauncherShim.PowerShell(cli));
        }
        else if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(sh, (UnixFileMode)0b111_101_101);
        }
    }

    private int Shortcut(bool dryRun, string profile, string block)
    {
        var done = false;
        var configured = Attempt("Failed to configure agent shortcut", () =>
        {
            var existing = ReadLf(profile);
            var updated = LauncherShim.Upsert(existing ?? string.Empty, block);
            if (existing is not null && updated == existing)
            {
                output.WriteLine($"Agent shortcut block in {Path.GetFileName(profile)} is already up to date.");
            }
            else if (dryRun)
            {
                output.WriteLine($"[dry-run] Would update agent shortcut in {profile}");
            }
            else
            {
                Put(profile, Eol(updated));
                done = true;
            }
        });
        if (done)
        {
            output.WriteLine($"OK: Agent shortcut configured in {Path.GetFileName(profile)}");
        }

        return configured ? 0 : 1;
    }

    // Compared with \n line ends; written with the platform's.
    private static string? ReadLf(string path) => File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;

    private string Eol(string text) => host.Windows ? text.ReplaceLineEndings("\r\n") : text;

    private static void Put(string path, string text)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text);
    }

    private bool Attempt(string failure, Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"{failure}: {e.Message}");
            return false;
        }
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
        var rulesPath = ClaudeRules.FilePath(home);
        if (SettingsFile.Load(path) is not { } existing)
        {
            error.WriteLine($"! {path} is not a readable JSON object; fix it by hand, then re-run.");
            return 1;
        }

        var merge = ClaudeBaseline.Merge(existing, SessionLedger.Command(hook));
        var stale = !SessionLedger.ScriptCurrent(hook);
        var rulesMerge = ClaudeRules.Merge(ReadLf(rulesPath));
        var rulesStale = rulesMerge.Action != ClaudeRulesAction.Unchanged;

        if (merge.Changes.Count == 0 && !stale && !rulesStale)
        {
            output.WriteLine($"✓ Claude Code settings up to date: {path}");
            output.WriteLine($"✓ Claude Code rules up to date: {rulesPath}");
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

        if (rulesStale)
        {
            output.WriteLine($"{(rulesMerge.Action == ClaudeRulesAction.Created ? "Creating" : "Updating")} Claude Code rules at {rulesPath}");
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
            if (!Attempt("Failed to write Claude settings", () => SettingsFile.Save(path, merge.Settings, host.Windows)))
            {
                return 1;
            }

            output.WriteLine($"✓ Successfully updated Claude Code settings at {path}");
        }

        if (rulesStale)
        {
            if (!Attempt("Failed to write Claude Code rules", () => ClaudeRules.Write(rulesPath, rulesMerge.Content, host.Windows)))
            {
                return 1;
            }

            output.WriteLine($"✓ Successfully updated Claude Code rules at {rulesPath}");
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
            if (path.EndsWith(".jsonc", StringComparison.Ordinal))
            {
                // JSONC carries comments a plain JSON merge would drop, and the projection would fail on them.
                // Leave the file to its author and point at the manual step instead of reporting a failure.
                output.WriteLine($"! Skipping {path}: it has comments, so its defaults and estate policy are left for you to apply by hand");
                return 0;
            }

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
            if (!Attempt("! Failed to write OpenCode config", () => SettingsFile.Save(path, merged, host.Windows)))
            {
                return 1;
            }

            output.WriteLine($"✓ Configured OpenCode default settings at {path}");
        }

        return Project("opencode", dryRun, home);
    }

    // The AGY global context in ~/.gemini/GEMINI.md: only the marked block is managed.
    private int Agy(bool dryRun, string home)
    {
        var target = AgyRules.FilePath(home);
        var merge = AgyRules.Merge(ReadLf(target));
        if (merge.Action == AgyRulesAction.Unchanged)
        {
            output.WriteLine($"✓ AGY global context up to date: {target}");
            return 0;
        }

        output.WriteLine($"{merge.Action} AGY global context at {target}");
        if (dryRun)
        {
            output.WriteLine("Dry run: no changes written.");
            return 0;
        }

        return Attempt("Failed to write AGY global context", () => Put(target, Eol(merge.Content))) ? 0 : 1;
    }

    // The estate-wide AGENTS.md: only the marked block is managed. 1 when the write fails.
    private int Rules(bool dryRun, string home)
    {
        var root = host.LayoutFor(home).Personal;
        if (!Directory.Exists(root))
        {
            output.WriteLine($"Estate root not present, skipping: {root}");
            return 0;
        }

        var target = Path.Join(root, "AGENTS.md");
        // Read and compared with \n line ends; written with the platform's.
        var merge = EstateRules.Merge(ReadLf(target));
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

        if (!Attempt("Failed to write estate rules", () => File.WriteAllText(target, Eol(merge.Content))))
        {
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
            await output.WriteLineAsync($"Not an Obsidian vault, skipping: {vault}", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        IReadOnlyList<PluginSpec> registry;
        try
        {
            registry = await plugins.RegistryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (ObsidianPlugins.Fetching(e))
        {
            await error.WriteLineAsync($"Could not fetch the Obsidian plugin registry: {e.Message}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var failed = false;
        foreach (var id in ObsidianPlugins.DefaultPlugins)
        {
            if (registry.FirstOrDefault(spec => spec.Id == id) is not { } spec)
            {
                await error.WriteLineAsync($"{id}: not in the community plugin registry", cancellationToken).ConfigureAwait(false);
                failed = true;
                continue;
            }

            var have = ObsidianPlugins.InstalledVersion(vault, id);
            var install = await plugins.InstallAsync(vault, spec, dryRun, cancellationToken).ConfigureAwait(false);
            if (install.Tag is not { } tag)
            {
                await error.WriteLineAsync(install.Problem, cancellationToken).ConfigureAwait(false);
                failed = true;
                continue;
            }

            await output.WriteLineAsync(have is not null && tag.Contains(have, StringComparison.Ordinal)
                ? $"✓ {spec.Name} {have} already installed"
                : $"{(dryRun ? "would install" : "installed")} {spec.Name} {tag}", cancellationToken).ConfigureAwait(false);
            if (ObsidianPlugins.Enable(vault, id, dryRun))
            {
                await output.WriteLineAsync($"  + enabled {id}", cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var id in ObsidianPlugins.EnableCore(vault, ObsidianPlugins.DefaultCorePlugins, dryRun))
        {
            await output.WriteLineAsync($"enabled core plugin: {id}", cancellationToken).ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            "Restart Obsidian to load newly installed plugins. Plugins needing an in-app API key must be configured in Obsidian's settings.", cancellationToken).ConfigureAwait(false);
        return failed ? 1 : 0;
    }

    // The estate policy's command rules, projected into the system's native config as `rig policy sync` does.
    private int Project(string system, bool dryRun, string home)
    {
        try
        {
            var result = NativeProjection.For(system, host.LayoutFor(home), host.Windows).Apply(CommandPolicy.Load(host.PolicyPath, host.Windows), dryRun);
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
