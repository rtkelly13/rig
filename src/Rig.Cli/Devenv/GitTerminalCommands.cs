using System.CommandLine;
using Rig.Core.Devenv;
using Rig.Core.Processes;

namespace Rig.Cli.Devenv;

/// <summary>
/// <c>rig devenv git</c> and <c>terminal</c>: the global git identity, defaults, per-folder identities and SSH key;
/// Windows Terminal's defaults and the Oh My Posh prompt hook. Ported from devenv's <c>steps/git.py</c> and
/// <c>steps/terminal.py</c>.
/// </summary>
/// <param name="host">Where it sets up, and where prompts are answered.</param>
/// <param name="proc">Runs git and ssh-keygen.</param>
/// <param name="output">Where progress goes.</param>
/// <param name="error">Where failures go.</param>
internal sealed class GitTerminalCommands(DevenvHost host, IProc proc, TextWriter output, TextWriter error)
{
    private static readonly string[] Modes = ["full", "express", "agent"];

    private TextReader Input => host.Input ?? TextReader.Null;

    /// <summary>The two subcommands, sharing <c>devenv</c>'s <c>--dry-run</c> and <c>--home</c>.</summary>
    public IEnumerable<Command> Build(Option<bool> dryRun, Option<string> home)
    {
        var mode = new Option<string>("--mode") { Description = "full (the default) prompts for a missing identity, per-folder identities and a new SSH key; express and agent never prompt.", DefaultValueFactory = _ => "full" };
        mode.AcceptOnlyFromAmong(Modes);
        var gitCheck = new Option<bool>("--check") { Description = "Report the git identity and SSH key, changing nothing; exit 1 when the identity is incomplete." };
        var git = new Command("git", "Set git's global defaults, route folders to identities from the devenv config, and check for an SSH key.") { dryRun, home, mode, gitCheck };
        git.SetAction((parsed, cancellationToken) => parsed.GetValue(gitCheck)
            ? GitCheckAsync(parsed.GetValue(home)!, cancellationToken)
            : GitAsync(parsed.GetValue(home)!, parsed.GetValue(dryRun), parsed.GetValue(mode) == "full", cancellationToken));
        var terminalCheck = new Option<bool>("--check") { Description = "Report whether Windows Terminal's snapToGridOnResize is off, changing nothing; exit 1 when it is not." };
        var terminal = new Command("terminal", "Set Windows Terminal's defaults and add the Oh My Posh prompt to the shell profile.") { dryRun, home, terminalCheck };
        terminal.SetAction(parsed => parsed.GetValue(terminalCheck) ? TerminalCheck(parsed.GetValue(home)!) : Terminal(parsed.GetValue(home)!, parsed.GetValue(dryRun)));
        return [git, terminal];
    }

    // The real home's git config is git's --global; a temporary --home's is its own .gitconfig, so it is never the real one.
    private string[] Scope(string home) => home == host.Home ? ["--global"] : ["--file", Path.Join(home, ".gitconfig")];

    private async Task<string?> GetAsync(string home, string key, CancellationToken cancellationToken)
    {
        var ran = await proc.RunAsync(new ProcRequest(["git", "config", .. Scope(home), "--get", key]), cancellationToken).ConfigureAwait(false);
        return ran.Ok && ran.Out.Trim() is { Length: > 0 } value ? value : null;
    }

    private async Task<bool> SetAsync(string home, string key, string value, CancellationToken cancellationToken)
    {
        var ran = await proc.RunAsync(new ProcRequest(["git", "config", .. Scope(home), key, value]), cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            await error.WriteLineAsync($"! git config {key} failed: {ran.Err.Trim()}").ConfigureAwait(false);
        }

        return ran.Ok;
    }

    private async Task<int> GitCheckAsync(string home, CancellationToken cancellationToken)
    {
        var name = await GetAsync(home, "user.name", cancellationToken).ConfigureAwait(false);
        var email = await GetAsync(home, "user.email", cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(name is not null && email is not null ? $"  Git identity: {name} <{email}>" : "  Git identity: incomplete (run `rig devenv git`)").ConfigureAwait(false);
        await output.WriteLineAsync($"  SSH key:      {GitSetup.SshKey(home) ?? "missing"}").ConfigureAwait(false);
        return name is not null && email is not null ? 0 : 1;
    }

    private async Task<int> GitAsync(string home, bool dryRun, bool prompt, CancellationToken cancellationToken)
    {
        var name = await GetAsync(home, "user.name", cancellationToken).ConfigureAwait(false);
        var email = await GetAsync(home, "user.email", cancellationToken).ConfigureAwait(false);
        if (name is not null && email is not null)
        {
            await output.WriteLineAsync($"✓ Current global Git identity: {name} <{email}>").ConfigureAwait(false);
        }
        else if (!prompt)
        {
            await output.WriteLineAsync("! Git user.name or user.email is not set. Run `rig devenv git`, or set them with git config --global.").ConfigureAwait(false);
            return 0;
        }
        else
        {
            name = await AskAsync("Enter your Git display name", name ?? "").ConfigureAwait(false);
            email = await AskAsync("Enter your Git email address", email ?? "").ConfigureAwait(false);
            if (!dryRun && name.Length > 0 && email.Length > 0
                && await SetAsync(home, "user.name", name, cancellationToken).ConfigureAwait(false)
                && await SetAsync(home, "user.email", email, cancellationToken).ConfigureAwait(false))
            {
                await output.WriteLineAsync($"✓ Configured global Git user: {name} <{email}>").ConfigureAwait(false);
            }
        }

        var failed = false;
        foreach (var (key, value) in GitSetup.Defaults(host.Windows))
        {
            if (dryRun)
            {
                await output.WriteLineAsync($"Dry run: would set git config {key} {value}").ConfigureAwait(false);
            }
            else
            {
                failed |= !await SetAsync(home, key, value, cancellationToken).ConfigureAwait(false);
            }
        }

        failed |= !await IdentitiesAsync(home, dryRun, prompt, name, email).ConfigureAwait(false);
        failed |= !await SshKeyAsync(home, dryRun, prompt, cancellationToken).ConfigureAwait(false);
        return failed ? 1 : 0;
    }

    private async Task<bool> IdentitiesAsync(string home, bool dryRun, bool prompt, string? name, string? email)
    {
        var identities = GitSetup.Identities(home);
        if (identities.Count > 0)
        {
            return await IncludesAsync(home, identities, dryRun).ConfigureAwait(false);
        }

        if (!prompt || !await ConfirmAsync("Would you like to configure directory-specific Git identities (e.g. personal vs work)?", false).ConfigureAwait(false))
        {
            return true;
        }

        var identity = new GitIdentity(
            await AskAsync("Identifier (e.g. personal, work)", "personal").ConfigureAwait(false),
            await AskAsync("Display Name", name ?? "").ConfigureAwait(false),
            await AskAsync("Email Address", email ?? "").ConfigureAwait(false),
            null,
            await AskAsync("Directory pattern to match (e.g. ~/code/personal/)", "~/code/personal/").ConfigureAwait(false));
        if (dryRun)
        {
            await output.WriteLineAsync($"Dry run: would save the identity to {GitSetup.ConfigPath(home)}").ConfigureAwait(false);
        }
        else if (!GitSetup.AddIdentity(home, identity, host.Windows))
        {
            await error.WriteLineAsync($"! {GitSetup.ConfigPath(home)} is not a JSON object; the identity was not saved.").ConfigureAwait(false);
            return false;
        }

        return await IncludesAsync(home, [identity], dryRun).ConfigureAwait(false);
    }

    private async Task<bool> IncludesAsync(string home, IReadOnlyList<GitIdentity> identities, bool dryRun)
    {
        foreach (var identity in identities.Where(i => !string.IsNullOrEmpty(i.MatchDirectory)))
        {
            var profile = Path.Join(home, $".gitconfig-{identity.Id}");
            if (dryRun)
            {
                await output.WriteLineAsync($"Dry run: would write {profile}").ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(profile, GitSetup.Profile(identity)).ConfigureAwait(false);
            }
        }

        var gitconfig = Path.Join(home, ".gitconfig");
        var existing = File.Exists(gitconfig) ? await File.ReadAllTextAsync(gitconfig).ConfigureAwait(false) : "";
        var updated = GitSetup.WithIncludes(existing, home, identities);
        if (updated == existing)
        {
            return true;
        }

        if (dryRun)
        {
            await output.WriteLineAsync($"Dry run: would add the identities' includeIf blocks to {gitconfig}").ConfigureAwait(false);
            return true;
        }

        await File.WriteAllTextAsync(gitconfig, updated).ConfigureAwait(false);
        await output.WriteLineAsync($"✓ Configured conditional Git directory routing in {gitconfig}").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SshKeyAsync(string home, bool dryRun, bool prompt, CancellationToken cancellationToken)
    {
        var ssh = Path.Join(home, ".ssh");
        if (GitSetup.SshKey(home) is not null)
        {
            await output.WriteLineAsync($"✓ SSH key found in {ssh}").ConfigureAwait(false);
            return true;
        }

        var key = Path.Join(ssh, "id_ed25519");
        await output.WriteLineAsync($"! No standard SSH key found in {ssh}").ConfigureAwait(false);
        if (dryRun)
        {
            await output.WriteLineAsync($"Dry run: would offer to generate an ed25519 key at {key}").ConfigureAwait(false);
            return true;
        }

        if (!prompt || !await ConfirmAsync("Would you like to generate a new ed25519 SSH key?", true).ConfigureAwait(false))
        {
            return true;
        }

        var email = await GetAsync(home, "user.email", cancellationToken).ConfigureAwait(false) ?? "user@localhost";
        _ = Directory.CreateDirectory(ssh);
        var ran = await proc.RunAsync(new ProcRequest(["ssh-keygen", "-t", "ed25519", "-C", email, "-f", key, "-N", ""]), cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            await error.WriteLineAsync($"! Failed to generate SSH key: {ran.Err.Trim()}").ConfigureAwait(false);
            return false;
        }

        await output.WriteLineAsync($"✓ Generated new SSH key at {key}").ConfigureAwait(false);
        var publicKey = key + ".pub";
        if (File.Exists(publicKey))
        {
            var text = (await File.ReadAllTextAsync(publicKey, cancellationToken).ConfigureAwait(false)).Trim();
            await output.WriteLineAsync($"\nPublic key (add it to GitHub -> Settings -> SSH Keys):\n{text}\n").ConfigureAwait(false);
        }

        return true;
    }

    private async Task<string> AskAsync(string question, string fallback)
    {
        await output.WriteAsync(fallback.Length > 0 ? $"{question} [{fallback}]: " : $"{question}: ").ConfigureAwait(false);
        var answer = (await Input.ReadLineAsync().ConfigureAwait(false))?.Trim();
        return string.IsNullOrEmpty(answer) ? fallback : answer;
    }

    private async Task<bool> ConfirmAsync(string question, bool fallback)
    {
        await output.WriteAsync($"{question} [{(fallback ? "Y/n" : "y/N")}]: ").ConfigureAwait(false);
        var answer = (await Input.ReadLineAsync().ConfigureAwait(false))?.Trim();
        return string.IsNullOrEmpty(answer) ? fallback : answer.StartsWith('y') || answer.StartsWith('Y');
    }

    private string? TerminalSettings(string home) =>
        host.Windows ? TerminalSetup.SettingsPath(home == host.Home && host.LocalAppData is { } local ? local : Path.Join(home, "AppData", "Local")) : null;

    private int TerminalCheck(string home)
    {
        if (!host.Windows)
        {
            output.WriteLine("  Windows Terminal: not used on this platform");
            return 0;
        }

        var status = TerminalSetup.Status(TerminalSettings(home));
        output.WriteLine(status switch
        {
            { Path: null } => "  Windows Terminal: not installed",
            { SnapToGrid: false } => "  Windows Terminal (snapToGrid: off)  ✓ configured",
            _ => "  Windows Terminal (snapToGrid: on)   ! jitter risk; run `rig devenv terminal`",
        });
        return status.Path is null || status.SnapToGrid == false ? 0 : 1;
    }

    private int Terminal(string home, bool dryRun)
    {
        var failed = host.Windows && !WindowsTerminal(home, dryRun);
        if (host.Find("oh-my-posh") is null)
        {
            output.WriteLine("! oh-my-posh is not installed. Install it with `rig devenv tools` first.");
            return failed ? 1 : 0;
        }

        var profile = host.Windows ? LauncherShim.PowerShellProfile(home) : LauncherShim.PosixProfile(home, host.Shell);
        output.WriteLine($"Target shell profile: {profile}");
        if (File.Exists(profile) && File.ReadAllText(profile).Contains("oh-my-posh", StringComparison.Ordinal))
        {
            output.WriteLine("✓ Oh My Posh is already configured in your shell profile.");
        }
        else if (dryRun)
        {
            output.WriteLine($"Dry run: would add the prompt hook to {profile}");
        }
        else
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.AppendAllText(profile, $"\n# Oh My Posh Prompt\n{TerminalSetup.PromptHook(profile, host.Windows)}\n");
            output.WriteLine($"✓ Added Oh My Posh initialization to {profile}");
        }

        return failed ? 1 : 0;
    }

    private bool WindowsTerminal(string home, bool dryRun)
    {
        if (TerminalSettings(home) is not { } path)
        {
            output.WriteLine("Windows Terminal not detected. Skipping terminal settings.");
            return true;
        }

        if (TerminalSetup.Load(path) is not { } settings)
        {
            error.WriteLine($"! {path} is not a JSON object; Windows Terminal was left alone.");
            return false;
        }

        var changes = TerminalSetup.Apply(settings, TerminalSetup.Font, host.CodeRoot);
        if (changes.Count == 0)
        {
            output.WriteLine("✓ Windows Terminal settings are already configured.");
            return true;
        }

        if (!dryRun)
        {
            try
            {
                SettingsFile.Save(path, settings, windows: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"! Failed to configure Windows Terminal: {e.Message}");
                return false;
            }
        }

        output.WriteLine(dryRun ? $"Dry run: would update {path}:" : $"✓ Updated Windows Terminal settings ({path}):");
        foreach (var change in changes)
        {
            output.WriteLine($"  • {change}");
        }

        return true;
    }
}
