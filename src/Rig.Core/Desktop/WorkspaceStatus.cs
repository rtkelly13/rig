using Rig.Core.Vault;

namespace Rig.Core.Desktop;

/// <summary>
/// The live workspace as a plan: what is open on each virtual desktop, and the draft launch entries an export writes.
/// </summary>
public static class WorkspaceStatus
{
    /// <summary>Processes whose windows a capture turns into workspace terminal entries.</summary>
    public static IReadOnlySet<string> WindowsTerminalProcesses { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "cmd.exe",
        "conhost.exe",
        "powershell.exe",
        "pwsh.exe",
        "windowsterminal.exe",
        "wt.exe",
    };

    /// <summary>What is open on the first <paramref name="desktopCount"/> desktops, named <c>Desktop N</c>.</summary>
    public static WorkspacePlan Current(IReadOnlyList<LiveWindow> windows, int desktopCount)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (desktopCount < 1)
        {
            throw new ArgumentException("Desktop count must be at least 1.", nameof(desktopCount));
        }

        var byDesktop = WindowsByDesktop(windows);
        return new()
        {
            Desktops =
            [
                .. Enumerable.Range(1, desktopCount)
                    .Select(number => new DesktopPlan($"Desktop {number}")
                    {
                        CapturedWindows = byDesktop.GetValueOrDefault(number, []),
                    }),
            ],
        };
    }

    /// <summary>What is open on the desktops numbered in <paramref name="desktopIndexes"/>, in that order.</summary>
    public static WorkspacePlan CurrentForDesktopIndexes(IReadOnlyList<LiveWindow> windows, IReadOnlyList<int> desktopIndexes)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(desktopIndexes);
        if (desktopIndexes.Count == 0)
        {
            return new() { Desktops = [] };
        }

        if (desktopIndexes.Any(number => number < 1))
        {
            throw new ArgumentException("Desktop indexes must be at least 1.", nameof(desktopIndexes));
        }

        var byDesktop = WindowsByDesktop(windows);
        return new()
        {
            Desktops =
            [
                .. desktopIndexes.Select(number => new DesktopPlan($"Desktop {number}")
                {
                    CapturedWindows = byDesktop.GetValueOrDefault(number, []),
                }),
            ],
        };
    }

    /// <summary>The windows on the current desktop, named <paramref name="desktopName"/> or "Current Desktop".</summary>
    public static DesktopPlan CurrentDesktop(IReadOnlyList<WindowSnapshot> windows, string? desktopName = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return new(desktopName ?? "Current Desktop") { CapturedWindows = windows };
    }

    /// <summary>
    /// A draft plan captured from the live desktops: every window becomes an app entry with its executable, except
    /// desktop 1's terminals, which become workspace terminal launchers when <paramref name="workspace"/> is given.
    /// </summary>
    public static WorkspacePlan Capture(IReadOnlyList<LiveWindow> windows, int desktopCount, string? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (desktopCount < 1)
        {
            throw new ArgumentException("Desktop count must be at least 1.", nameof(desktopCount));
        }

        var byDesktop = WindowsByDesktop(windows);
        return new()
        {
            Workspace = workspace,
            Desktops =
            [
                .. Enumerable.Range(1, desktopCount).Select(number =>
                {
                    var windows = byDesktop.GetValueOrDefault(number, []);
                    return new DesktopPlan($"Desktop {number}")
                    {
                        Launch = LaunchEntriesFor(windows, number, workspace),
                        CapturedWindows = windows,
                    };
                }),
            ],
            Notes =
            [
                "Captured launch commands are a draft. Review browser paths, URLs, and profile-specific arguments before using restore.",
                "Desktop 1 terminal windows are converted into workspace terminal launchers when a workspace is supplied.",
            ],
        };
    }

    /// <summary>
    /// The draft entries for one desktop's captured windows. Desktop 1's terminals become numbered workspace terminals
    /// when a workspace is supplied; every other window with an executable becomes an app entry.
    /// </summary>
    public static IReadOnlyList<LaunchEntry> LaunchEntriesFor(IReadOnlyList<WindowSnapshot> windows, int desktopNumber, string? workspace)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var entries = new List<LaunchEntry>();
        var terminalIndex = 0;
        foreach (var window in windows)
        {
            var processName = UnicodeText.Lower(window.ProcessName ?? string.Empty);
            if (desktopNumber == 1 && workspace is not null && WindowsTerminalProcesses.Contains(processName))
            {
                terminalIndex++;
                var placement = window.InferredPlacement ?? TerminalPlacement(terminalIndex);
                entries.Add(new($"Workspace Terminal {terminalIndex}")
                {
                    Type = LaunchEntry.Terminal,
                    Cwd = "{workspace}",
                    Command = InferredTerminalCommand(window.Title),
                    Shell = LaunchEntry.DefaultTerminalShell,
                    WindowTitle = $"Workspace Terminal {terminalIndex}",
                    Placement = placement,
                    WindowProcessName = "WindowsTerminal.exe",
                    Notes = $"Generated from captured terminal window: {window.Title}",
                });
                continue;
            }

            if (!string.IsNullOrEmpty(window.ExePath))
            {
                entries.Add(new(window.Title)
                {
                    Type = "app",
                    Path = window.ExePath,
                    Placement = window.InferredPlacement,
                    WindowProcessName = window.ProcessName,
                    Notes = "Captured from the current desktop state. Add args if this app needs a project, URL, or profile.",
                });
            }
        }

        return entries;
    }

    private static PlacementSpec TerminalPlacement(int index)
    {
        var slot = Math.Min(index, 3);
        if (index is 2 or 3)
        {
            return new(2, new("thirds", slot), new("halves", index == 2 ? 1 : 2, "horizontal"));
        }

        return new(2, new("thirds", slot));
    }

    private static string InferredTerminalCommand(string windowTitle) =>
        UnicodeText.CaseFold(windowTitle).Contains("copilot", StringComparison.Ordinal) ? "copilot" : "opencode";

    // Reusable windows on numbered desktops, each desktop's sorted by process name, then title.
    private static Dictionary<int, IReadOnlyList<WindowSnapshot>> WindowsByDesktop(IReadOnlyList<LiveWindow> windows) =>
        windows
            .Where(window => window.DesktopNumber is >= 1)
            .GroupBy(window => window.DesktopNumber!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<WindowSnapshot>)
                [
                    .. group
                        .Select(Snapshot)
                        .OrderBy(window => UnicodeText.Lower(window.ProcessName ?? string.Empty), StringComparer.Ordinal)
                        .ThenBy(window => UnicodeText.Lower(window.Title), StringComparer.Ordinal),
                ]);

    private static WindowSnapshot Snapshot(LiveWindow window) =>
        new(
            window.Title,
            window.Pid,
            window.ProcessName,
            window.ExePath,
            window.ClassName,
            window.ScreenNumber,
            window.InferredPlacement,
            window.Bounds);
}
