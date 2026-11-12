using Rig.Core.Vault;

namespace Rig.Core.Desktop;

/// <summary>
/// One screen of one desktop: what the plan puts there, what is there, and how they pair up. Ported from
/// <c>desktop_manager.visualization.ScreenDiff</c>.
/// </summary>
/// <remarks>
/// <see cref="Missing"/> and <see cref="Misplaced"/> are planned entries another restore attempt can fix.
/// <see cref="Extra"/> (a window no entry claims) and <see cref="Borrowed"/> (a window matched to an entry planned for
/// another screen) are not: a retry cannot close a stray window.
/// </remarks>
/// <param name="ScreenNumber">The screen, from 1.</param>
public sealed record ScreenDiff(int ScreenNumber)
{
    /// <summary>The entries the plan puts on this screen.</summary>
    public IReadOnlyList<string> Planned { get; init; } = [];

    /// <summary>The windows on it now.</summary>
    public IReadOnlyList<string> Actual { get; init; } = [];

    /// <summary>Planned entries and the window on this screen each matched.</summary>
    public IReadOnlyList<string> Matched { get; init; } = [];

    /// <summary>Planned entries whose window is on another screen.</summary>
    public IReadOnlyList<string> Misplaced { get; init; } = [];

    /// <summary>Windows here that belong to an entry planned for another screen.</summary>
    public IReadOnlyList<string> Borrowed { get; init; } = [];

    /// <summary>Planned entries with no window at all.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>Windows here no entry claims.</summary>
    public IReadOnlyList<string> Extra { get; init; } = [];

    /// <summary>Whether the screen matches its plan.</summary>
    public bool IsOk => Missing.Count == 0 && Extra.Count == 0 && Misplaced.Count == 0 && Borrowed.Count == 0;

    /// <summary>Whether a planned entry here is missing or on the wrong screen, which another attempt can fix.</summary>
    public bool HasFixableDrift => Missing.Count > 0 || Misplaced.Count > 0;

    /// <summary>The screen's lines in the rendered diff.</summary>
    public IReadOnlyList<string> RenderLines()
    {
        var lines = new List<string>
        {
            $"Screen {ScreenNumber}",
            $"  planned: {Joined(Planned)}",
            $"  actual:  {Joined(Actual)}",
        };
        AddIfAny(lines, "  matched: ", Matched);
        AddIfAny(lines, "  misplaced: ", Misplaced);
        AddIfAny(lines, "  borrowed: ", Borrowed);
        if (IsOk)
        {
            lines.Add("  status:  ok");
        }
        else
        {
            AddIfAny(lines, "  missing: ", Missing);
            AddIfAny(lines, "  extra:   ", Extra);
        }

        return lines;
    }

    /// <inheritdoc/>
    public bool Equals(ScreenDiff? other) =>
        other is not null
        && ScreenNumber == other.ScreenNumber
        && Planned.SequenceEqual(other.Planned, StringComparer.Ordinal)
        && Actual.SequenceEqual(other.Actual, StringComparer.Ordinal)
        && Matched.SequenceEqual(other.Matched, StringComparer.Ordinal)
        && Misplaced.SequenceEqual(other.Misplaced, StringComparer.Ordinal)
        && Borrowed.SequenceEqual(other.Borrowed, StringComparer.Ordinal)
        && Missing.SequenceEqual(other.Missing, StringComparer.Ordinal)
        && Extra.SequenceEqual(other.Extra, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ScreenNumber, Planned.Count, Actual.Count, Missing.Count, Extra.Count);

    private static string Joined(IReadOnlyList<string> names) => names.Count > 0 ? string.Join(", ", names) : "-";

    private static void AddIfAny(List<string> lines, string prefix, IReadOnlyList<string> names)
    {
        if (names.Count > 0)
        {
            lines.Add(prefix + string.Join(", ", names));
        }
    }
}

/// <summary>One planned desktop, screen by screen. Ported from <c>desktop_manager.visualization.DesktopDiff</c>.</summary>
/// <param name="Name">The desktop's name.</param>
/// <param name="Screens">Its screens, in number order.</param>
public sealed record DesktopDiff(string Name, IReadOnlyList<ScreenDiff> Screens)
{
    /// <inheritdoc/>
    public bool Equals(DesktopDiff? other) =>
        other is not null && Name == other.Name && Screens.SequenceEqual(other.Screens);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Name, Screens.Count);
}

/// <summary>
/// The plan against the live workspace. Decisions read the fields; people read <see cref="Render"/>. Ported from
/// <c>desktop_manager.visualization</c>.
/// </summary>
/// <param name="Desktops">Each planned desktop's diff, in plan order.</param>
public sealed record WorkspaceDiff(IReadOnlyList<DesktopDiff> Desktops)
{
    /// <summary>Every screen of every desktop.</summary>
    public IEnumerable<ScreenDiff> Screens => Desktops.SelectMany(desktop => desktop.Screens);

    /// <summary>Nothing missing, misplaced, borrowed or extra anywhere.</summary>
    public bool IsClean => Screens.All(screen => screen.IsOk);

    /// <summary>Some planned entry is missing or on the wrong screen, which another attempt can fix.</summary>
    public bool HasFixableDrift => Screens.Any(screen => screen.HasFixableDrift);

    /// <summary>Compare <paramref name="plan"/>'s desktops with <paramref name="actual"/>'s, position by position.</summary>
    public static WorkspaceDiff Of(WorkspacePlan plan, WorkspacePlan actual, IReadOnlyDictionary<string, string> substitutions)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(actual);
        var desktops = new List<DesktopDiff>();
        for (var index = 0; index < plan.Desktops.Count; index++)
        {
            var planned = plan.Desktops[index];
            var live = index < actual.Desktops.Count ? actual.Desktops[index] : new DesktopPlan($"Desktop {index + 1}");
            desktops.Add(new(planned.Name, DiffDesktop(planned, live, substitutions)));
        }

        return new(desktops);
    }

    /// <summary>
    /// Names of planned entries already matched to a window on their planned screen: the structured form of the
    /// <c>status: ok</c> and <c>matched:</c> lines, so a retry can skip what is already right. An entry matched on the
    /// wrong screen is left out, since a retry can still move it.
    /// </summary>
    public static IReadOnlySet<string> EntriesMatchedOnPlannedScreen(DesktopPlan planned, DesktopPlan actual, IReadOnlyDictionary<string, string> substitutions)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(actual);
        var satisfied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entryIndex, windowIndex) in Match(planned, actual, substitutions).ByEntry)
        {
            var entry = planned.Launch[entryIndex];
            if (entry.Placement is { } placement && actual.CapturedWindows[windowIndex].ScreenNumber == placement.Screen)
            {
                satisfied.Add(entry.Name);
            }
        }

        return satisfied;
    }

    /// <summary><paramref name="value"/> cut to <paramref name="limit"/> code points, ending <c>...</c> when cut.</summary>
    public static string TruncateText(string value, int limit)
    {
        ArgumentNullException.ThrowIfNull(value);
        return PyText.Length(value) <= limit ? value : PyText.Prefix(value, limit - 3) + "...";
    }

    /// <summary>How a captured window reads in the diff: its title and process stem, with its pid.</summary>
    public static string WindowLabel(WindowSnapshot window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var process = string.IsNullOrEmpty(window.ProcessName) ? null : WindowsStem(window.ProcessName);
        var pid = window.Pid is { } known ? $" pid={known}" : string.Empty;
        return (window.Title.Length > 0, process) switch
        {
            (true, not null) => TruncateText($"{window.Title} [{process}{pid}]", 64),
            (true, null) => TruncateText(window.Title + pid, 64),
            (false, not null) => process + pid,
            _ => "<unknown window>",
        };
    }

    /// <summary>Only the screens that differ from <paramref name="previous"/>; this whole diff when there is none or nothing changed.</summary>
    public WorkspaceDiff ChangedSince(WorkspaceDiff? previous)
    {
        if (previous is null)
        {
            return this;
        }

        var before = previous.Desktops
            .SelectMany(desktop => desktop.Screens.Select(screen => (Key: (desktop.Name, screen.ScreenNumber), screen)))
            .GroupBy(pair => pair.Key)
            .ToDictionary(group => group.Key, group => group.Last().screen);
        var changed = Desktops
            .Select(desktop => new DesktopDiff(
                desktop.Name,
                [.. desktop.Screens.Where(screen => !before.TryGetValue((desktop.Name, screen.ScreenNumber), out var old) || old != screen)]))
            .Where(desktop => desktop.Screens.Count > 0)
            .ToList();
        return changed.Count > 0 ? new(changed) : this;
    }

    /// <summary>The diff as people read it.</summary>
    public string Render()
    {
        var lines = new List<string> { "Workspace Diff" };
        foreach (var desktop in Desktops)
        {
            lines.Add(string.Empty);
            lines.Add(desktop.Name);
            foreach (var screen in desktop.Screens)
            {
                lines.AddRange(screen.RenderLines());
            }
        }

        return string.Join('\n', lines);
    }

    /// <inheritdoc/>
    public bool Equals(WorkspaceDiff? other) => other is not null && Desktops.SequenceEqual(other.Desktops);

    /// <inheritdoc/>
    public override int GetHashCode() => Desktops.Count;

    private static List<ScreenDiff> DiffDesktop(DesktopPlan planned, DesktopPlan actual, IReadOnlyDictionary<string, string> substitutions)
    {
        var matches = Match(planned, actual, substitutions);
        var windows = actual.CapturedWindows;
        var plannedByScreen = new SortedDictionary<int, List<string>>();
        foreach (var entry in planned.Launch.Where(entry => !entry.Disabled && entry.Placement is not null))
        {
            Bucket(plannedByScreen, entry.Placement!.Screen).Add(entry.Name);
        }

        var actualByScreen = new SortedDictionary<int, List<string>>();
        foreach (var window in windows.Where(window => window.ScreenNumber is not null))
        {
            Bucket(actualByScreen, window.ScreenNumber!.Value).Add(WindowLabel(window));
        }

        var screens = new List<ScreenDiff>();
        foreach (var screen in plannedByScreen.Keys.Union(actualByScreen.Keys).Order())
        {
            var entryIndexes = Enumerable.Range(0, planned.Launch.Count)
                .Where(index => planned.Launch[index] is { Disabled: false, Placement: { } placement } && placement.Screen == screen)
                .ToList();
            var windowIndexes = Enumerable.Range(0, windows.Count).Where(index => windows[index].ScreenNumber == screen).ToList();
            var matched = new List<string>();
            var misplaced = new List<string>();
            var missing = new List<string>();
            foreach (var entryIndex in entryIndexes)
            {
                var entry = planned.Launch[entryIndex];
                if (!matches.ByEntry.TryGetValue(entryIndex, out var windowIndex))
                {
                    missing.Add(entry.Name);
                }
                else if (windows[windowIndex].ScreenNumber == screen)
                {
                    matched.Add($"{entry.Name} <- {WindowLabel(windows[windowIndex])}");
                }
                else
                {
                    var actualScreen = windows[windowIndex].ScreenNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                    misplaced.Add($"{entry.Name} -> {WindowLabel(windows[windowIndex])} on screen {actualScreen} (planned {screen})");
                }
            }

            var borrowed = new List<string>();
            var extra = new List<string>();
            foreach (var windowIndex in windowIndexes)
            {
                if (!matches.ByWindow.TryGetValue(windowIndex, out var entryIndex))
                {
                    extra.Add(WindowLabel(windows[windowIndex]));
                    continue;
                }

                var owner = planned.Launch[entryIndex];
                if (owner.Placement is { } placement && placement.Screen != screen)
                {
                    borrowed.Add($"{WindowLabel(windows[windowIndex])} -> {owner.Name} on screen {placement.Screen}");
                }
            }

            screens.Add(new(screen)
            {
                Planned = plannedByScreen.GetValueOrDefault(screen) ?? [],
                Actual = actualByScreen.GetValueOrDefault(screen) ?? [],
                Matched = matched,
                Misplaced = misplaced,
                Borrowed = borrowed,
                Missing = missing,
                Extra = extra,
            });
        }

        return screens;
    }

    private static List<string> Bucket(SortedDictionary<int, List<string>> buckets, int screen)
    {
        if (!buckets.TryGetValue(screen, out var names))
        {
            names = [];
            buckets[screen] = names;
        }

        return names;
    }

    // Each entry takes its best-scoring window, best pair first; Python sorts (score, entry, window) descending, so a
    // tie goes to the later entry, then the later window.
    private static (Dictionary<int, int> ByEntry, Dictionary<int, int> ByWindow) Match(
        DesktopPlan planned,
        DesktopPlan actual,
        IReadOnlyDictionary<string, string> substitutions)
    {
        var scored = new List<(int Score, int Entry, int Window)>();
        for (var entryIndex = 0; entryIndex < planned.Launch.Count; entryIndex++)
        {
            var entry = planned.Launch[entryIndex];
            if (entry.Disabled)
            {
                continue;
            }

            var competing = WindowReuse.CompetingSiblingTitles(planned.Launch, entry);
            for (var windowIndex = 0; windowIndex < actual.CapturedWindows.Count; windowIndex++)
            {
                var score = WindowReuse.CapturedWindowMatchScore(entry, actual.CapturedWindows[windowIndex], substitutions, competing);
                if (score > 0)
                {
                    scored.Add((score, entryIndex, windowIndex));
                }
            }
        }

        var byEntry = new Dictionary<int, int>();
        var byWindow = new Dictionary<int, int>();
        foreach (var (_, entryIndex, windowIndex) in scored.OrderDescending())
        {
            if (byEntry.ContainsKey(entryIndex) || byWindow.ContainsKey(windowIndex))
            {
                continue;
            }

            byEntry[entryIndex] = windowIndex;
            byWindow[windowIndex] = entryIndex;
        }

        return (byEntry, byWindow);
    }

    // PureWindowsPath(name).stem: the file name without its last suffix, unless the dot leads or ends it.
    private static string WindowsStem(string path)
    {
        var name = WindowReuse.WindowsFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[..dot] : name;
    }
}
