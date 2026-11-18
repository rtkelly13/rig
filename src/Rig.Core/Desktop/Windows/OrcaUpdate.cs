using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Rig.Core.Desktop.Windows;

/// <summary>The installer electron-updater left in its pending folder.</summary>
/// <param name="Version">Its file version, or null when the file carries none.</param>
/// <param name="Installer">The installer's full path.</param>
/// <param name="Sha512">The base64 SHA-512 electron-updater recorded.</param>
/// <param name="AdminRequired">Whether it needs elevation, which the update refuses to do unattended.</param>
[ExcludeFromCodeCoverage(Justification = "Windows Orca update state; exercised by Windows CI.")]
public sealed record StagedOrcaUpdate(string? Version, string Installer, string? Sha512, bool AdminRequired);

/// <summary>What the disk and process table say about an Orca update.</summary>
/// <param name="InstalledVersion">The installed build, when it can be read.</param>
/// <param name="Staged">The staged installer, when there is one.</param>
/// <param name="OrcaRunning">Whether any Orca process is running.</param>
/// <param name="InstallerRunning">Whether an Orca installer is already running.</param>
[ExcludeFromCodeCoverage(Justification = "Windows Orca update state; exercised by Windows CI.")]
public sealed record OrcaUpdateState(string? InstalledVersion, StagedOrcaUpdate? Staged, bool OrcaRunning, bool InstallerRunning)
{
    /// <summary>The staged build, when one is staged.</summary>
    public string? StagedVersion => Staged?.Version;

    /// <summary>Whether a strictly newer build is staged and Orca is installed.</summary>
    public bool Pending => VersionKey(StagedVersion) is { } staged
        && VersionKey(InstalledVersion) is { } installed
        && staged.CompareTo(installed) > 0;

    /// <summary><c>1.4.250</c> and <c>1.4.250.0</c> compare equal; anything non-numeric gives null.</summary>
    public static Version? VersionKey(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Trim().TrimStart('v', 'V').Split('.');
        var numbers = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers.Add(number);
        }

        while (numbers.Count > 1 && numbers[^1] == 0)
        {
            numbers.RemoveAt(numbers.Count - 1);
        }

        return Version.TryParse(string.Join('.', numbers), out var key) ? key : null;
    }

    /// <summary>One line describing what was found.</summary>
    public string Describe() =>
        $"installed={InstalledVersion ?? "?"} staged={StagedVersion ?? "none"} "
        + $"orca_running={OrcaRunning.ToString().ToLowerInvariant()} "
        + $"installer_running={InstallerRunning.ToString().ToLowerInvariant()} pending={Pending.ToString().ToLowerInvariant()}";
}

/// <summary>What applying an Orca update did, for the command to print and turn into an exit code.</summary>
[ExcludeFromCodeCoverage(Justification = "Windows Orca update state; exercised by Windows CI.")]
public sealed record OrcaUpdateResult(IReadOnlyList<string> Actions, bool Ok = true);

/// <summary>
/// Applying a staged Orca update within the plan: close every Orca, install the staged build silently, then let the
/// caller relaunch every Orca entry.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Reads the Windows registry and process table; the Windows CI job smoke-tests it.")]
public static class OrcaUpdate
{
    /// <summary>The process name Orca runs under.</summary>
    public const string ProcessName = "orca.exe";

    /// <summary>The display name of its uninstall entry.</summary>
    public const string DisplayName = "Orca";

    private const string InstallerHint = "orca-windows-setup";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SelfInstallAppear = TimeSpan.FromSeconds(15);

    /// <summary>An entry that starts Orca: a plan entry with its process or path ending in <c>Orca.exe</c>.</summary>
    public static bool EntryTargetsOrca(LaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Equals(entry.WindowProcessName, ProcessName, StringComparison.OrdinalIgnoreCase)
            || (entry.Path?.EndsWith(ProcessName, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Every enabled entry that starts Orca, with its desktop and 1-based index, in plan order.</summary>
    public static IReadOnlyList<(int DesktopIndex, DesktopPlan Desktop, LaunchEntry Entry)> Entries(WorkspacePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. plan.Desktops.SelectMany((desktop, index) => desktop.Launch
            .Where(entry => !entry.Disabled && EntryTargetsOrca(entry))
            .Select(entry => (index + 1, desktop, entry)))];
    }

    /// <summary>The disk and process state of an Orca update.</summary>
    public static OrcaUpdateState State(string localAppData, WindowInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(localAppData);
        ArgumentNullException.ThrowIfNull(inventory);
        var running = inventory.RunningProcessNames();
        return new(
            InstalledVersion(localAppData),
            Staged(localAppData),
            running.Contains(ProcessName),
            running.Any(name => name.Contains(InstallerHint, StringComparison.Ordinal)));
    }

    /// <summary>The installed build: its uninstall entry's display version, else <c>Orca.exe</c>'s file version.</summary>
    public static string? InstalledVersion(string localAppData)
    {
        ArgumentNullException.ThrowIfNull(localAppData);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is string display
                        && string.Equals(display, DisplayName, StringComparison.OrdinalIgnoreCase)
                        && entry.GetValue("DisplayVersion") is string version
                        && version.Length > 0)
                    {
                        return version;
                    }
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // An unreadable hive is not a failure; the file version below still answers.
            }
        }

        var executable = Path.Join(localAppData, "Programs", "orca", "Orca.exe");
        return File.Exists(executable) ? FileVersion(executable) : null;
    }

    /// <summary>The pending installer, versioned by its own file version.</summary>
    public static StagedOrcaUpdate? Staged(string localAppData)
    {
        ArgumentNullException.ThrowIfNull(localAppData);
        var pending = Path.Join(localAppData, "orca-updater", "pending");
        JsonNode? info;
        try
        {
            info = JsonNode.Parse(File.ReadAllText(Path.Join(pending, "update-info.json")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }

        if (info is not JsonObject data || data["fileName"] is not JsonValue fileNameValue
            || fileNameValue.GetValueKind() is not System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        var fileName = Path.GetFileName(fileNameValue.GetValue<string>());
        var installer = Path.Join(pending, fileName);
        if (!File.Exists(installer))
        {
            return null;
        }

        var sha = data["sha512"] is JsonValue shaValue && shaValue.GetValueKind() is System.Text.Json.JsonValueKind.String
            ? shaValue.GetValue<string>()
            : null;
        var admin = data["isAdminRightsRequired"] is JsonValue adminValue && adminValue.GetValueKind() is System.Text.Json.JsonValueKind.True;
        return new(FileVersion(installer), installer, sha, admin);
    }

    /// <summary>Whether the installer matches the base64 SHA-512 electron-updater recorded.</summary>
    public static bool MatchesSha512(string installer, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        byte[] wanted;
        try
        {
            wanted = Convert.FromBase64String(expected);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(installer);
            return SHA512.HashData(stream).AsSpan().SequenceEqual(wanted);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Wait until no Orca installer process remains; whether it finished in time.</summary>
    public static bool WaitForInstaller(WindowInventory inventory, TimeSpan timeout, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!InstallerRunning(inventory))
            {
                return true;
            }

            Thread.Sleep(Poll);
        }

        log?.Invoke($"Orca installer still running after {timeout.TotalSeconds:F0}s; continuing anyway");
        return false;
    }

    /// <summary>Verify and silently run the staged installer. It does not relaunch Orca; the caller does.</summary>
    public static bool RunInstaller(StagedOrcaUpdate staged, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.AdminRequired)
        {
            log?.Invoke("Orca installer needs admin rights; not running it unattended");
            return false;
        }

        if (!MatchesSha512(staged.Installer, staged.Sha512))
        {
            log?.Invoke($"Orca installer {staged.Installer} does not match its recorded SHA-512; not running it");
            return false;
        }

        log?.Invoke($"Running Orca installer {staged.Installer} ({staged.Version ?? "?"}) silently");
        var start = new ProcessStartInfo(staged.Installer)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--updated", "/S" },
        };
        using var process = Process.Start(start);
        if (process is null || !process.WaitForExit(InstallTimeout))
        {
            log?.Invoke("Orca installer did not finish in time");
            return false;
        }

        if (process.ExitCode != 0)
        {
            log?.Invoke($"Orca installer exited {process.ExitCode}");
            return false;
        }

        return true;
    }

    /// <summary>Close every Orca, install a staged update if behind, and report what happened.</summary>
    public static OrcaUpdateResult Apply(
        WorkspacePlan plan,
        WindowInventory inventory,
        WindowActions actions,
        string localAppData,
        bool dryRun,
        IRunLog log,
        TimeSpan? closeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(log);
        var timeout = closeTimeout ?? TimeSpan.FromSeconds(60);
        var result = new List<string>();

        var state = State(localAppData, inventory);
        log.Log($"Orca update check: {state.Describe()}");
        var updateInFlight = state.InstallerRunning;
        if (updateInFlight && !dryRun)
        {
            result.Add("Orca installer already running; waiting for it to finish");
            if (!WaitForInstaller(inventory, InstallTimeout, log.Log))
            {
                result.Add(InstallerStillRunning);
                return new(result, false);
            }

            state = State(localAppData, inventory);
        }

        if (!updateInFlight && (!state.Pending || state.Staged is null))
        {
            result.Add($"Orca is up to date (installed {state.InstalledVersion ?? "?"}); nothing to do");
            return new(result);
        }

        var entries = Entries(plan);
        var labels = entries.Count > 0 ? string.Join(", ", entries.Select(item => $"{item.Desktop.Name}/{item.Entry.Name}")) : "none";
        result.Add(state.Pending && state.Staged is not null
            ? $"Orca update {state.InstalledVersion ?? "?"} -> {state.StagedVersion}; entries to relaunch: {labels}"
            : $"Orca installer finished (installed {state.InstalledVersion ?? "?"}); entries to relaunch: {labels}");
        if (dryRun)
        {
            result.Add($"  - would close every Orca window (graceful, up to {timeout.TotalSeconds:F0}s)");
            if (state is { Staged: not null, Pending: true })
            {
                result.Add($"  - would run {state.Staged.Installer} --updated /S");
            }

            return new([.. result, .. entries.Select(item => $"  - would relaunch `{item.Entry.Name}` on {item.Desktop.Name}")]);
        }

        result.Add($"  - {CloseAll(inventory, actions, timeout, log.Log)}");

        // Orca may hand off to its own installer as it quits; let that finish rather than racing it.
        if (InstallerAppears(inventory, SelfInstallAppear))
        {
            result.Add("  - Orca started its own installer on quit; waiting for it");
            if (!WaitForInstaller(inventory, InstallTimeout, log.Log))
            {
                result.Add(InstallerStillRunning);
                return new(result, false);
            }
        }

        state = State(localAppData, inventory);
        if (state.InstallerRunning)
        {
            result.Add(InstallerStillRunning);
            return new(result, false);
        }

        var ok = true;
        if (state is { Pending: true, Staged: not null })
        {
            if (RunInstaller(state.Staged, log.Log))
            {
                result.Add($"  - installed {state.StagedVersion}");
            }
            else
            {
                result.Add("  - installer did not complete; relaunching Orca on the current build");
                ok = false;
            }
        }

        var installed = InstalledVersion(localAppData);
        log.Log($"Orca installed version is now {installed ?? "?"}");
        result.Add($"  - installed version is now {installed ?? "?"}");
        return new(result, ok);
    }

    /// <summary>Ask every Orca window to close, then force-kill whatever is still alive after the timeout.</summary>
    public static string CloseAll(WindowInventory inventory, WindowActions actions, TimeSpan timeout, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(actions);
        var handles = inventory.WindowsMatchingProcess(ProcessName, currentDesktopOnly: false);
        foreach (var handle in handles)
        {
            actions.RequestClose(handle);
        }

        log?.Invoke($"Requested WM_CLOSE for {handles.Count} Orca window(s)");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (inventory.ProcessIdsForImageName(ProcessName).Count == 0)
            {
                return $"closed {handles.Count} Orca window(s) gracefully";
            }

            Thread.Sleep(Poll);
        }

        var survivors = inventory.ProcessIdsForImageName(ProcessName);
        var killed = survivors.Count(pid => actions.TerminateProcess(pid));
        foreach (var pid in survivors)
        {
            _ = actions.WaitForProcessExit(pid, TimeSpan.FromSeconds(10));
        }

        log?.Invoke($"Orca still running after {timeout.TotalSeconds:F0}s; force-killed {killed} process(es)");
        return $"requested close for {handles.Count} Orca window(s); force-killed {killed} survivor(s)";
    }

    private const string InstallerStillRunning =
        "  - an Orca installer is still running after the wait; not installing or relaunching. "
        + "Re-run `rig desktop workspace update-orca` once it exits";

    private static bool InstallerRunning(WindowInventory inventory) =>
        inventory.RunningProcessNames().Any(name => name.Contains(InstallerHint, StringComparison.Ordinal));

    private static bool InstallerAppears(WindowInventory inventory, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (InstallerRunning(inventory))
            {
                return true;
            }

            Thread.Sleep(Poll);
        }

        return false;
    }

    private static string? FileVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }
}
