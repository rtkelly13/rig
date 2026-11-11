using Rig.Core.Processes;

namespace Rig.Core.Browser;

/// <summary>Where a user-scoped environment variable is kept between sessions.</summary>
public interface IUserEnvironment
{
    /// <summary>The persisted value of <paramref name="name"/>, or null when it is not set.</summary>
    string? Read(string name);

    /// <summary>Persists <paramref name="name"/>=<paramref name="value"/>; throws <see cref="IOException"/> when it cannot.</summary>
    void Write(string name, string value);
}

/// <summary>macOS and Linux: <c>export</c> lines in the shell profile.</summary>
/// <param name="profile">The shell profile file, such as <c>~/.zshrc</c>.</param>
public sealed class ShellProfileEnvironment(string profile) : IUserEnvironment
{
    /// <inheritdoc />
    public string? Read(string name)
    {
        if (!File.Exists(profile))
        {
            return null;
        }

        var prefix = $"export {name}=";
        return File.ReadLines(profile)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..].Trim().Trim('"'))
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public void Write(string name, string value) => File.AppendAllText(profile, $"\nexport {name}=\"{value}\"\n");
}

/// <summary>The folders and files agent-browser setup touches, all under one home.</summary>
/// <param name="Home">The user's home folder.</param>
/// <param name="Shell">The login shell (<c>SHELL</c>), which picks the profile the variables are exported from.</param>
/// <param name="Windows">Whether this is Windows, where shortcuts go in the PowerShell profile.</param>
public sealed record BrowserPlaces(string Home, string? Shell, bool Windows)
{
    /// <summary>The dedicated profiles, separate from the daily Chrome: dev is the default, personal is opt-in per command.</summary>
    public static readonly IReadOnlyList<string> ProfileNames = ["dev", "personal"];

    /// <summary>The root of the dedicated agent-browser Chrome profiles; one folder per profile.</summary>
    public string ProfilesRoot => Path.Join(Home, ".agent-browser-profiles");

    /// <summary>The default profile's folder.</summary>
    public string DevProfile => Path.Join(ProfilesRoot, "dev");

    /// <summary>The shell profile the variables are exported from on macOS and Linux.</summary>
    public string ShellProfile => Path.Join(Home, (Shell ?? "/bin/bash").Contains("zsh", StringComparison.Ordinal) ? ".zshrc" : ".bashrc");

    /// <summary>The profile the per-profile shortcuts are added to.</summary>
    public string ShortcutsProfile =>
        Windows ? Path.Join(Home, "Documents", "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1") : Path.Join(Home, ".zshrc");
}

/// <summary>How many variables setup wrote, and how many were already set and left alone.</summary>
/// <param name="Applied">Variables written.</param>
/// <param name="Skipped">Variables already set.</param>
public sealed record BrowserEnvOutcome(int Applied, int Skipped);

/// <summary>
/// Provisions what the upstream <c>agent-browser</c> CLI reads: user-scoped defaults (never-clobber), the dedicated
/// profile folders and per-profile shell shortcuts. Ported from <c>rig.agent_browser</c> and devenv's agent-browser step.
/// </summary>
/// <param name="places">The home being set up.</param>
/// <param name="processEnvironment">Reads this process's environment, checked before the persisted store.</param>
/// <param name="store">Where user-scoped variables persist.</param>
/// <param name="proc">Runs the daemon probe.</param>
public sealed class BrowserSetup(BrowserPlaces places, Func<string, string?> processEnvironment, IUserEnvironment store, IProc proc)
{
    /// <summary>The variable naming the default profile.</summary>
    public const string ProfileVariable = "AGENT_BROWSER_PROFILE";

    /// <summary>The variable that reuses a running daily Chrome; off by default.</summary>
    public const string AutoConnectVariable = "AGENT_BROWSER_AUTO_CONNECT";

    /// <summary>The marker line the shortcuts are added under, so they are added once.</summary>
    public const string ShortcutsMarker = "# Agent Browser Helpers";

    /// <summary>The variables setup wants, in the order they are written.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Baseline =>
    [
        new(AutoConnectVariable, "0"),
        new("AGENT_BROWSER_HEADED", "1"),
        new(ProfileVariable, places.DevProfile),
    ];

    /// <summary>The value of <paramref name="name"/> in this process, else in the persisted store, else null.</summary>
    public string? Current(string name) => processEnvironment(name) is { Length: > 0 } value ? value : store.Read(name);

    /// <summary>
    /// Writes the baseline variables that are not set yet (all of them with <paramref name="force"/>), reporting through
    /// <paramref name="say"/>. A dry run reports the plan and writes nothing.
    /// </summary>
    public async Task<BrowserEnvOutcome> ApplyEnvironmentAsync(bool dryRun, bool force, Action<string> say, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(say);
        var target = Baseline;
        var changes = force ? target : [.. target.Where(v => Current(v.Key) is null)];
        if (Current(AutoConnectVariable) == "1")
        {
            say($"⚠ {AutoConnectVariable} is already set to '1' (the previous default). This step will not overwrite it. "
                + "To adopt the dedicated-profile default, set these at User scope by hand and open a new terminal:\n"
                + $"  {AutoConnectVariable}=0\n  {ProfileVariable}={places.DevProfile}");
        }

        var skipped = target.Count - changes.Count;
        if (changes.Count == 0)
        {
            say("✓ agent-browser environment already configured.");
            say(SignInHint);
            return new BrowserEnvOutcome(0, skipped);
        }

        say("Planned user-scope environment variables:");
        foreach (var (name, value) in changes)
        {
            say($"  + {name}={value}");
        }

        say(string.Empty);
        if (dryRun)
        {
            say("Dry run — no changes written.");
            return new BrowserEnvOutcome(0, skipped);
        }

        var applied = changes.Count(v => Persist(v.Key, v.Value, say));
        if (applied > 0)
        {
            say($"✓ Applied {applied} agent-browser environment setting(s). Open a new terminal for them to take effect.");
            if (await DaemonRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                say("⚠ An agent-browser daemon is already running and will keep using its current profile until it is restarted. "
                    + "Close it so the new settings take effect on the next run:\n  agent-browser close --all\n"
                    + "(This only shuts down agent-browser's own Chrome-for-Testing; it will not close your daily Chrome unless a run used --auto-connect.)");
            }

            say(SignInHint);
        }

        return new BrowserEnvOutcome(applied, skipped);
    }

    /// <summary>
    /// Whether an <c>agent-browser</c> daemon is running: it binds the profile only at startup, so a running one ignores
    /// new variables. Only looks; a failed probe answers false, which at worst leaves out a hint.
    /// </summary>
    public async Task<bool> DaemonRunningAsync(CancellationToken cancellationToken)
    {
        string[] argv = places.Windows
            ? ["powershell", "-NoProfile", "-Command", "Get-Process -Name agent-browser -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { $_.Id }"]
            : ["pgrep", "-f", "agent-browser"];
        var ran = await proc.RunAsync(new ProcRequest(argv), cancellationToken).ConfigureAwait(false);
        return ran.OutOrEmpty.Length > 0;
    }

    /// <summary>Creates the profile folders and adds the per-profile shortcuts to the shell profile, once.</summary>
    public void ProvisionProfiles(bool dryRun, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);
        if (dryRun)
        {
            say($"Dry run: would ensure {places.ProfilesRoot} profiles exist");
            return;
        }

        foreach (var name in BrowserPlaces.ProfileNames)
        {
            _ = Directory.CreateDirectory(Path.Join(places.ProfilesRoot, name));
        }

        say($"✓ Agent-browser profiles initialized in {places.ProfilesRoot}");
        AddShortcuts(say);
    }

    private string SignInHint =>
        "Dedicated profile is the default. Sign into the services you need in it once (a headed window opens; complete any sign-in, then close):\n"
        + $"  agent-browser open --profile {places.DevProfile} --headed <url>\n"
        + "For an ad-hoc check against a third-party site already open in your daily Chrome, opt into auto-connect for that run: "
        + $"{AutoConnectVariable}=1 agent-browser open <url>";

    // The shortcuts override the default profile for one command; the default itself is the environment's job.
    private string Shortcuts => places.Windows
        ? $"\n{ShortcutsMarker}\n"
            + "function agent-browser-dev { agent-browser --profile \"$HOME\\.agent-browser-profiles\\dev\" @args }\n"
            + "function agent-browser-personal { agent-browser --profile \"$HOME\\.agent-browser-profiles\\personal\" @args }\n"
        : $"\n{ShortcutsMarker}\n"
            + "alias agent-browser-dev=\"agent-browser --profile $HOME/.agent-browser-profiles/dev\"\n"
            + "alias agent-browser-personal=\"agent-browser --profile $HOME/.agent-browser-profiles/personal\"\n";

    private void AddShortcuts(Action<string> say)
    {
        var profile = places.ShortcutsProfile;
        if (!File.Exists(profile))
        {
            return;
        }

        try
        {
            if (!File.ReadAllText(profile).Contains(ShortcutsMarker, StringComparison.Ordinal))
            {
                File.AppendAllText(profile, Shortcuts);
                say($"✓ Injected Agent Browser shell helpers into {Path.GetFileName(profile)}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            say($"Could not update shell profile for agent browser: {e.Message}");
        }
    }

    private bool Persist(string name, string value, Action<string> say)
    {
        try
        {
            store.Write(name, value);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            say(places.Windows ? $"✗ Failed to set {name}: {e.Message}" : $"✗ Failed to update shell profile: {e.Message}");
            return false;
        }
    }
}

/// <summary>Windows: the User-scope environment in the registry, which needs no admin.</summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Windows registry only; BrowserSetup is tested through IUserEnvironment.")]
public sealed class WindowsUserEnvironment : IUserEnvironment
{
    /// <inheritdoc />
    public string? Read(string name) => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is { Length: > 0 } value ? value : null;

    /// <inheritdoc />
    public void Write(string name, string value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
}
