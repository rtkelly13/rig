using System.Text.Json.Nodes;
using Rig.Core.Vault;

namespace Rig.Core.Desktop;

/// <summary>The live window claimed for an entry, if any, and the windows still unclaimed.</summary>
/// <param name="Window">The claimed window, or null when none matched.</param>
/// <param name="Remaining">Every other window, in their original order.</param>
public sealed record ReuseClaim(LiveWindow? Window, IReadOnlyList<LiveWindow> Remaining);

/// <summary>
/// Matching windows to plan entries by title, process, pid file and position. Ported from the platform-free half of
/// <c>desktop_manager.window_reuse</c>; moving and confirming windows are <see cref="IDesktopPort"/> members.
/// </summary>
/// <remarks>
/// Where a score needs a placement's pixels for a window known only by its bounds, the caller passes
/// <c>placementBounds</c> (Python read the live monitors there). Without it that bonus is left out.
/// </remarks>
public static class WindowReuse
{
    private const string Chrome = "chrome.exe";

    /// <summary>The window in <paramref name="windows"/> that best fits <paramref name="entry"/>, or null when none fits at all.</summary>
    /// <param name="entry">The entry to find a window for.</param>
    /// <param name="windows">The candidates; the first of equal best scores wins.</param>
    /// <param name="substitutions">The plan's placeholders.</param>
    /// <param name="targetDesktop">The entry's desktop: windows known to be elsewhere only qualify by pid.</param>
    /// <param name="competingTitles">Sibling entries' titles, from <see cref="CompetingSiblingTitles"/>.</param>
    /// <param name="placementBounds">A placement's pixels on the live screens.</param>
    public static LiveWindow? MatchReusableWindow(
        LaunchEntry entry,
        IEnumerable<LiveWindow> windows,
        IReadOnlyDictionary<string, string> substitutions,
        int? targetDesktop = null,
        IReadOnlySet<string>? competingTitles = null,
        Func<PlacementSpec, Rect>? placementBounds = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(windows);
        var candidates = targetDesktop is null
            ? windows
            : windows.Where(window =>
                window.DesktopNumber is null
                || window.DesktopNumber == targetDesktop
                || WindowPidFile.PidVerified(entry, window.Pid, substitutions));

        LiveWindow? best = null;
        var bestScore = 0;
        foreach (var window in candidates)
        {
            var score = ReusableWindowMatchScore(entry, window, substitutions, targetDesktop, competingTitles, placementBounds);
            if (score > bestScore)
            {
                (best, bestScore) = (window, score);
            }
        }

        return best;
    }

    /// <summary><see cref="MatchReusableWindow"/>, and the windows left once it is taken.</summary>
    /// <inheritdoc cref="MatchReusableWindow" path="/param"/>
    public static ReuseClaim ClaimReusableWindow(
        LaunchEntry entry,
        IReadOnlyList<LiveWindow> windows,
        IReadOnlyDictionary<string, string> substitutions,
        int? targetDesktop = null,
        IReadOnlySet<string>? competingTitles = null,
        Func<PlacementSpec, Rect>? placementBounds = null)
    {
        var match = MatchReusableWindow(entry, windows, substitutions, targetDesktop, competingTitles, placementBounds);
        return match is null
            ? new(null, [.. windows])
            : new(match, [.. windows.Where(window => window.Handle != match.Handle)]);
    }

    /// <summary>How well <paramref name="window"/> fits <paramref name="entry"/>: 0 when it cannot be the entry's window.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="window">The candidate window.</param>
    /// <param name="substitutions">The plan's placeholders.</param>
    /// <param name="targetDesktop">The entry's desktop: a window known to be elsewhere only scores when its pid is verified.</param>
    /// <param name="competingTitles">Sibling entries' titles, from <see cref="CompetingSiblingTitles"/>.</param>
    /// <param name="placementBounds">A placement's pixels on the live screens.</param>
    public static int ReusableWindowMatchScore(
        LaunchEntry entry,
        LiveWindow window,
        IReadOnlyDictionary<string, string> substitutions,
        int? targetDesktop = null,
        IReadOnlySet<string>? competingTitles = null,
        Func<PlacementSpec, Rect>? placementBounds = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(window);
        var score = WindowIdentityMatchScore(entry, window.Title, window.ProcessName, substitutions, competingTitles);
        if (score <= 0 || !WindowPidFile.WindowOwnerAllowed(entry, window.Pid, substitutions))
        {
            return 0;
        }

        if (targetDesktop is { } target
            && window.DesktopNumber is { } desktop
            && desktop != target
            && !WindowPidFile.PidVerified(entry, window.Pid, substitutions))
        {
            return 0;
        }

        if (targetDesktop is not null && window.DesktopNumber == targetDesktop)
        {
            score += 120;
        }

        if (entry.Placement is not { } placement)
        {
            return score;
        }

        if (window.ScreenNumber == placement.Screen)
        {
            score += 80;
        }

        if (window.InferredPlacement is { } inferred)
        {
            score += SamePlacement(placement, inferred) ? 140
                : Geometry.NormalizedPlacementRect(placement) == Geometry.NormalizedPlacementRect(inferred) ? 90
                : 0;
        }
        else
        {
            score += BoundsBonus(window.Bounds, placement, placementBounds, 60);
        }

        if (entry.Type == LaunchEntry.Terminal)
        {
            if (window.ScreenNumber == placement.Screen)
            {
                score += 80;
            }

            score += window.InferredPlacement is { } terminalInferred && SamePlacement(terminalInferred, placement)
                ? 180
                : BoundsBonus(window.Bounds, placement, placementBounds, 140);
        }

        return score;
    }

    /// <summary>How well a window captured in a plan fits <paramref name="entry"/>: 0 when it cannot be the entry's window.</summary>
    public static int CapturedWindowMatchScore(
        LaunchEntry entry,
        WindowSnapshot window,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlySet<string>? competingTitles = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(window);
        var score = WindowIdentityMatchScore(entry, window.Title, window.ProcessName, substitutions, competingTitles);
        if (score <= 0 || !WindowPidFile.WindowOwnerAllowed(entry, window.Pid, substitutions))
        {
            return 0;
        }

        if (entry.Placement is not { } placement)
        {
            return score;
        }

        if (window.ScreenNumber == placement.Screen)
        {
            score += 80;
        }

        if (window.InferredPlacement is { } inferred)
        {
            score += SamePlacement(placement, inferred) ? 120
                : Geometry.NormalizedPlacementRect(placement) == Geometry.NormalizedPlacementRect(inferred) ? 80
                : 0;
        }

        return score;
    }

    /// <summary>
    /// The case-folded window titles of the sibling entries that could own the same window as <paramref name="current"/>,
    /// leaving out its own title. Stops a title-less entry claiming a window that plainly belongs to a sibling by title,
    /// such as a VS Code "Workspace" entry grabbing a "prs-to-review.md" window on its process alone. A sibling running a
    /// different process can never own the window, so it is no competition; when either process is unknown it is.
    /// </summary>
    public static IReadOnlySet<string> CompetingSiblingTitles(IEnumerable<LaunchEntry> entries, LaunchEntry current)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(current);
        var ownTitle = Fold(current.WindowTitle);
        var ownProcess = Fold(current.WindowProcessName);
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in entries)
        {
            if (ReferenceEquals(other, current))
            {
                continue;
            }

            var otherProcess = Fold(other.WindowProcessName);
            if (ownProcess.Length > 0 && otherProcess.Length > 0 && ownProcess != otherProcess)
            {
                continue;
            }

            var title = Fold(other.WindowTitle);
            if (title.Length > 0 && title != ownTitle)
            {
                titles.Add(title);
            }
        }

        return titles;
    }

    /// <summary>
    /// How strongly a window's title and process say it is <paramref name="entry"/>'s, before position counts: 0 when
    /// they rule it out.
    /// </summary>
    public static int WindowIdentityMatchScore(
        LaunchEntry entry,
        string windowTitle,
        string? windowProcessName,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlySet<string>? competingTitles = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(windowTitle);
        var targetTitle = Fold(entry.WindowTitle);
        var title = PyText.CaseFold(windowTitle);

        // A declared process is the author saying which process owns the window, so a mismatch rules it out; the
        // fallback from the launch path is only a guess, and a window whose process is unknown stays eligible.
        var declared = Fold(entry.WindowProcessName);
        if (declared.Length > 0 && !string.IsNullOrEmpty(windowProcessName) && declared != PyText.CaseFold(windowProcessName))
        {
            return 0;
        }

        if (competingTitles is { Count: > 0 } && title.Length > 0)
        {
            var matchesOwn = targetTitle.Length > 0 && title.Contains(targetTitle, StringComparison.Ordinal);
            if (!matchesOwn && competingTitles.Any(sibling => title.Contains(sibling, StringComparison.Ordinal)))
            {
                return 0;
            }
        }

        var score = 0;
        if (targetTitle.Length > 0)
        {
            score += title == targetTitle ? 200 : title.Contains(targetTitle, StringComparison.Ordinal) ? 150 : 0;
        }

        var chromeHintScore = IsChromeEntry(entry, substitutions) ? ChromeTitleHintScore(entry, title, substitutions) : 0;
        score += chromeHintScore;

        var processTarget = ExpectedProcessName(entry, substitutions);
        var processMatched = !string.IsNullOrEmpty(processTarget)
            && !string.IsNullOrEmpty(windowProcessName)
            && processTarget == PyText.CaseFold(windowProcessName);
        if (processMatched)
        {
            score += 100;
        }

        score += TerminalCommandHintScore(entry, title);

        var entryName = PyText.CaseFold(entry.Name);
        if (entryName.Length > 0
            && title.Contains(entryName, StringComparison.Ordinal)
            && !(!string.IsNullOrEmpty(windowProcessName) && PyText.CaseFold(windowProcessName) == Chrome))
        {
            score += 25;
        }

        // A launch URL is the only strong identity for a Chrome entry, but titles drift ("New Tab" once the tab
        // closes), so a window with no hint falls back to a weak score and lets position decide.
        if (ChromeTitleHints(entry, substitutions).Count > 0 && chromeHintScore <= 0 && targetTitle.Length == 0)
        {
            return processMatched ? 1 : 0;
        }

        return score;
    }

    /// <summary>The one-line account of what launching <paramref name="entry"/> runs, for plans and logs.</summary>
    public static string DescribeLaunch(LaunchEntry entry, IReadOnlyDictionary<string, string> substitutions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Type == LaunchEntry.Terminal)
        {
            // Python prints a missing directory as None.
            var directory = Placeholders.Substitute(entry.Cwd, substitutions) ?? "None";
            return $"terminal `{entry.Name}` -> {Placeholders.Substitute(entry.Command, substitutions)} (cwd: {directory})";
        }

        if (entry.Type == "app")
        {
            var path = Placeholders.Substitute(entry.Path, substitutions) is { Length: > 0 } resolved ? resolved : "<missing path>";
            var args = string.Join(' ', entry.Args.Select(arg => Placeholders.Substitute(arg, substitutions)).Where(arg => arg.Length > 0));
            return $"app `{entry.Name}` -> {path}{(args.Length > 0 ? " " + args : string.Empty)}";
        }

        var command = Placeholders.Substitute(entry.Command, substitutions) is { Length: > 0 } text ? text : "<missing command>";
        var cwd = Placeholders.Substitute(entry.Cwd, substitutions);
        return string.IsNullOrEmpty(cwd) ? $"command `{entry.Name}` -> {command}" : $"command `{entry.Name}` -> {command} (cwd: {cwd})";
    }

    /// <summary>
    /// The case-folded process name <paramref name="entry"/>'s window should have: its declared process, else its launch
    /// path's file name (split as a Windows path on every machine), else null.
    /// </summary>
    public static string? ExpectedProcessName(LaunchEntry entry, IReadOnlyDictionary<string, string> substitutions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrEmpty(entry.WindowProcessName))
        {
            return PyText.CaseFold(entry.WindowProcessName);
        }

        var path = Placeholders.Substitute(entry.Path, substitutions);
        return string.IsNullOrEmpty(path) ? null : PyText.CaseFold(WindowsFileName(path));
    }

    /// <summary>The last component of a Windows path, as <c>PureWindowsPath(path).name</c> gives it.</summary>
    public static string WindowsFileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = path.TrimEnd('\\', '/');
        var name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0]) ? name[2..] : name;
    }

    /// <summary>Whether two placements are the same as a plan file writes them; the runtime column count does not count.</summary>
    public static bool SamePlacement(PlacementSpec a, PlacementSpec b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return JsonNode.DeepEquals(a.ToJson(), b.ToJson());
    }

    private static string Fold(string? value) => PyText.CaseFold(PyText.Strip(value ?? string.Empty));

    private static int BoundsBonus(Rect? bounds, PlacementSpec placement, Func<PlacementSpec, Rect>? placementBounds, int weight) =>
        bounds is not null && placementBounds is not null
            ? (int)(Geometry.RectSimilarity(bounds, placementBounds(placement)) * weight)
            : 0;

    private static int TerminalCommandHintScore(LaunchEntry entry, string title)
    {
        if (entry.Type != LaunchEntry.Terminal || string.IsNullOrEmpty(entry.Command))
        {
            return 0;
        }

        var command = PyText.CaseFold(entry.Command);
        if (command.Contains("copilot", StringComparison.Ordinal))
        {
            return title.Contains("copilot", StringComparison.Ordinal) ? 180 : 0;
        }

        if (command.Contains("claude", StringComparison.Ordinal))
        {
            return title.Contains("claude", StringComparison.Ordinal) ? 180 : title.StartsWith("⠐ ", StringComparison.Ordinal) ? 160 : 0;
        }

        if (command.Contains("opencode", StringComparison.Ordinal))
        {
            return title.Contains("opencode", StringComparison.Ordinal) ? 180 : title.StartsWith("oc |", StringComparison.Ordinal) ? 160 : 0;
        }

        return 0;
    }

    private static bool IsChromeEntry(LaunchEntry entry, IReadOnlyDictionary<string, string> substitutions) =>
        ExpectedProcessName(entry, substitutions) == Chrome
        || (Placeholders.Substitute(entry.Path, substitutions) is { Length: > 0 } path && PyText.CaseFold(path).EndsWith(Chrome, StringComparison.Ordinal));

    private static int ChromeTitleHintScore(LaunchEntry entry, string title, IReadOnlyDictionary<string, string> substitutions)
    {
        var best = 0;
        foreach (var hint in ChromeTitleHints(entry, substitutions))
        {
            var folded = PyText.CaseFold(hint);
            best = Math.Max(best, title == folded ? 220 : title.Contains(folded, StringComparison.Ordinal) ? 170 : 0);
        }

        return best;
    }

    private static List<string> ChromeTitleHints(LaunchEntry entry, IReadOnlyDictionary<string, string> substitutions)
    {
        var hints = new List<string>();
        foreach (var arg in entry.Args)
        {
            var url = Placeholders.Substitute(arg, substitutions);
            if (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal))
            {
                hints.AddRange(ChromeHintsForUrl(url));
            }
        }

        return [.. hints.Distinct(StringComparer.Ordinal)];
    }

    private static List<string> ChromeHintsForUrl(string url)
    {
        var (netloc, path) = SplitUrl(url);
        var host = PyText.CaseFold(netloc);
        path = PyText.CaseFold(path);
        if (host.Contains("meet.google.com", StringComparison.Ordinal))
        {
            return ["google meet", "meet"];
        }

        if (host.Contains("calendar.google.com", StringComparison.Ordinal))
        {
            return ["google calendar", "calendar"];
        }

        if (host.Contains("mail.google.com", StringComparison.Ordinal) || host == "gmail.com")
        {
            return ["gmail", "mail"];
        }

        if (host.Contains("docs.google.com", StringComparison.Ordinal))
        {
            return ["google docs", "docs"];
        }

        if (host.Length == 0)
        {
            return [];
        }

        var labels = host.Split('.').Where(label => label is not ("www" or "com")).ToList();
        var hints = labels.Count > 0 ? new List<string> { PyText.Strip(string.Join(' ', labels.Take(2))), labels[0] } : [];
        if (path.Length > 0 && path != "/")
        {
            hints.Add(path.Trim('/').Split('/')[0]);
        }

        return [.. hints.Where(hint => hint.Length > 0)];
    }

    // urllib.parse.urlparse's netloc and path for an http(s) URL: unsafe tab and newline characters are dropped,
    // trailing C0 controls and spaces stripped, and the last segment's ;params cut from the path.
    private static (string Netloc, string Path) SplitUrl(string url)
    {
        url = url.Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .TrimEnd([.. Enumerable.Range(0, 33).Select(code => (char)code)]);
        var rest = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var netlocEnd = rest.IndexOfAny(['/', '?', '#']);
        var netloc = netlocEnd < 0 ? rest : rest[..netlocEnd];
        var path = netlocEnd < 0 ? string.Empty : rest[netlocEnd..];
        var pathEnd = path.IndexOfAny(['?', '#']);
        path = pathEnd < 0 ? path : path[..pathEnd];
        var paramsAt = path.IndexOf(';', Math.Max(path.LastIndexOf('/'), 0));
        return (netloc, paramsAt < 0 ? path : path[..paramsAt]);
    }
}
