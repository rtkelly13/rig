using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Core.Chrome;

/// <summary>
/// A tab's identity for the session: UI Automation's RuntimeId on Windows, the driver's own ids elsewhere. Never the
/// index, which shifts as tabs close, nor the selection, which an action changes.
/// </summary>
public sealed class RuntimeId : IEquatable<RuntimeId>
{
    /// <summary>Wraps <paramref name="parts"/>, copied.</summary>
    public RuntimeId(IEnumerable<int> parts) => Parts = [.. parts ?? throw new ArgumentNullException(nameof(parts))];

    /// <summary>The id's numbers, in order.</summary>
    public IReadOnlyList<int> Parts { get; }

    /// <inheritdoc/>
    public bool Equals(RuntimeId? other) => other is not null && Parts.SequenceEqual(other.Parts);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as RuntimeId);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var part in Parts)
        {
            hash.Add(part);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => string.Join('.', Parts);
}

/// <summary>A tab's accessible name, split into the title and what Chrome appended.</summary>
/// <param name="Title">The page title.</param>
/// <param name="Loaded">The tab carries a memory suffix, so it is loaded rather than discarded.</param>
/// <param name="Alerts">The alert suffixes, in the order Chrome wrote them.</param>
/// <param name="MemoryMb">The renderer's memory footprint on a loaded tab; null when unloaded.</param>
/// <param name="Pinned">Chrome labelled the tab pinned. Builds without the label never set this.</param>
public sealed record TabName(string Title, bool Loaded, IReadOnlyList<string> Alerts, double? MemoryMb = null, bool Pinned = false)
{
    /// <summary>Any of the alerts is live media, so nothing should close the tab.</summary>
    public bool Audible => Alerts.Any(TabNames.AudibleAlerts.Contains);
}

/// <summary>
/// What Chrome packs into a tab's or a group header's accessible name: the title, then optional alert suffixes, a
/// "Part of group" suffix and, on a loaded tab, a memory suffix. Chrome's English strings only; another UI language
/// leaves the tab looking unloaded with a longer title. Ported from <c>desktop_manager.chrome.tabs</c>.
/// </summary>
public static partial class TabNames
{
    private static readonly string[] PinnedSuffixes = [" - Pinned", " – Pinned"];
    private static readonly string[] Dashes = ["-", "–"];

    private static readonly string[] OtherAlerts =
    [
        "Connected to a Bluetooth device",
        "Connected to a USB device",
        "Connected to a serial port",
        "Connected to a HID device",
        "Network error",
        "Crashed",
    ];

    /// <summary>Chrome's live-media alert suffixes (<c>IDS_TAB_AX_LABEL_*_FORMAT</c>): a tab with any is audible.</summary>
    public static IReadOnlyList<string> AudibleAlerts { get; } =
    [
        "Audio playing",
        "Audio muted",
        "Camera or microphone recording",
        "Camera and microphone recording",
        "Microphone recording",
        "Camera recording",
        "Tab content shared",
        "Desktop content shared",
        "Picture-in-picture",
        "Sending content to headset",
    ];

    /// <summary>The alerts that say the user is on a call or showing their screen: an unattended run leaves Chrome alone.</summary>
    public static IReadOnlySet<string> LiveAlerts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Camera or microphone recording",
        "Camera and microphone recording",
        "Microphone recording",
        "Camera recording",
        "Tab content shared",
        "Desktop content shared",
    };

    private static IEnumerable<string> AllAlerts => AudibleAlerts.Concat(OtherAlerts);

    /// <summary>Strip Chrome's memory, pinned, group and alert suffixes off a tab's accessible name.</summary>
    public static TabName Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = InactiveSuffix().Replace(RStrip(name), string.Empty);
        var loaded = false;
        double? memoryMb = null;
        var memory = MemorySuffix().Match(text);
        if (memory.Success)
        {
            loaded = true;
            var amount = double.Parse(memory.Groups["amount"].Value.Replace(",", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);
            memoryMb = amount * UnitMb(memory.Groups["unit"].Value);
            text = text[..memory.Index];
        }

        var alerts = new List<string>();
        var pinned = false;
        var stripped = true;
        while (stripped)
        {
            stripped = false;
            if (PinnedSuffixes.Any(suffix => text.EndsWith(suffix, StringComparison.Ordinal)))
            {
                pinned = true;
                text = text[..^PinnedSuffixes[0].Length];
                stripped = true;
                continue;
            }

            var group = GroupSuffix().Match(text);
            if (group.Success && !EndsWithAlert(text))
            {
                text = text[..group.Index];
                stripped = true;
                continue;
            }

            foreach (var suffix in AllAlerts.SelectMany(alert => Dashes.Select(dash => (alert, suffix: $" {dash} {alert}"))))
            {
                if (text.EndsWith(suffix.suffix, StringComparison.Ordinal))
                {
                    alerts.Insert(0, suffix.alert);
                    text = text[..^suffix.suffix.Length];
                    stripped = true;
                    break;
                }
            }
        }

        return new TabName(text, loaded, alerts, memoryMb, pinned);
    }

    /// <summary>A tab group's title from its header's accessible name; "" for an unnamed group, null if unrecognised.</summary>
    public static string? ParseGroupHeader(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var match = GroupHeader().Match(name);
        return match.Success ? match.Groups["title"].Value : null;
    }

    private static bool EndsWithAlert(string text) =>
        AllAlerts.Any(alert => Dashes.Any(dash => text.EndsWith($" {dash} {alert}", StringComparison.Ordinal)));

    private static double UnitMb(string unit) => unit switch
    {
        "B" => 1 / (1024.0 * 1024.0),
        "KB" => 1 / 1024.0,
        "GB" => 1024.0,
        "TB" => 1024.0 * 1024.0,
        _ => 1.0,
    };

    private static string RStrip(string text)
    {
        var end = text.Length;
        while (end > 0 && Vault.PyText.IsSpace(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    [GeneratedRegex(@" [-–] (?:High memory usage|Memory usage) [-–] (?<amount>[\d.,]+)\s?(?<unit>[KMGT]?B)$")]
    private static partial Regex MemorySuffix();

    [GeneratedRegex(@" [-–] Part of (?:unnamed group|group .*)$")]
    private static partial Regex GroupSuffix();

    // macOS Chrome's Memory Saver label on a discarded tab.
    [GeneratedRegex(@" [-–] Inactive tab [-–] [\d.,]+\s?[KMGT]?B freed up$")]
    private static partial Regex InactiveSuffix();

    // " group <title> – 3 tabs, •  <tab titles> – Expanded", or " unnamed group – ..." when the group has no title.
    [GeneratedRegex(@"^\s*(?:unnamed group|group (?<title>.*?)) – \d+ [Tt]abs?\b")]
    private static partial Regex GroupHeader();
}

/// <summary>The tab group a tab sits in. <paramref name="Title"/> is "" for a group nobody has named.</summary>
/// <param name="RuntimeId">The group view's identity.</param>
/// <param name="Title">The title its header shows.</param>
public sealed record TabGroup(RuntimeId RuntimeId, string Title);

/// <summary>One tab-strip item. <paramref name="RuntimeId"/> is the only stable identity across a session.</summary>
/// <param name="RuntimeId">The tab's identity.</param>
/// <param name="WindowHandle">The browser window holding it.</param>
/// <param name="Index">Visual position in its window, 0-based. Shifts as tabs close; never an identity.</param>
/// <param name="Name">The raw accessible name.</param>
/// <param name="Title">The page title parsed from it.</param>
/// <param name="Selected">The window's active tab.</param>
/// <param name="Pinned">Pinned, by label or by its container.</param>
/// <param name="Loaded">Not discarded.</param>
/// <param name="Audible">Playing or recording live media.</param>
public sealed record ChromeTab(
    RuntimeId RuntimeId,
    long WindowHandle,
    int Index,
    string Name,
    string Title,
    bool Selected,
    bool Pinned,
    bool Loaded,
    bool Audible)
{
    /// <summary>Chrome's alert suffixes on the tab.</summary>
    public IReadOnlyList<string> Alerts { get; init; } = [];

    /// <summary>The memory Chrome reports for a loaded tab; null when unloaded.</summary>
    public double? MemoryMb { get; init; }

    /// <summary>The group holding it, if any.</summary>
    public TabGroup? Group { get; init; }

    /// <summary>The page address where the platform exposes one (macOS scripting); UI Automation does not.</summary>
    public Uri? Url { get; init; }
}

/// <summary>A point in virtual-screen pixels; monitors left of or above the primary go negative.</summary>
/// <param name="X">Pixels from the primary's left edge.</param>
/// <param name="Y">Pixels from the primary's top edge.</param>
public sealed record ScreenPoint(int X, int Y);

/// <summary>A window's edges in screen pixels.</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
public sealed record WindowRect(int Left, int Top, int Right, int Bottom);

/// <summary>A top-level Chrome browser window and its tabs, in tab-strip order.</summary>
/// <param name="Handle">The window handle.</param>
/// <param name="Title">The window title.</param>
/// <param name="ProfileDir">Chrome's profile directory ("Default", "Profile 4"), from the relaunch command.</param>
/// <param name="ProfileName">The profile's display name, from the relaunch display name.</param>
/// <param name="AgeRank">
/// Creation order across the process's windows, lower is older: Chrome assigns accessibility ids in creation order, so
/// it is a heuristic, not a timestamp.
/// </param>
/// <param name="Minimized">Minimised.</param>
/// <param name="DesktopNumber">The virtual desktop it is on, where desktops are addressable.</param>
/// <param name="Tabs">Its tabs, in strip order.</param>
public sealed record ChromeWindow(
    long Handle,
    string Title,
    string? ProfileDir,
    string? ProfileName,
    long AgeRank,
    bool Minimized,
    int? DesktopNumber,
    IReadOnlyList<ChromeTab> Tabs)
{
    /// <summary>The restored position when minimised.</summary>
    public WindowRect? Rect { get; init; }

    /// <summary>The top-left corner of the monitor the window is on.</summary>
    public ScreenPoint? Monitor { get; init; }

    /// <summary>
    /// Set by a driver that cannot tie the window to a known profile (or it is private), so it is planned on its own
    /// instead of sharing one "(unknown profile)" scope.
    /// </summary>
    public string? ProfileScope { get; init; }

    /// <summary>The scope plans are made in: the driver's scope, else the profile directory.</summary>
    public string ProfileKey => ProfileScope ?? ProfileDir ?? "(unknown profile)";

    /// <summary>The active tab, if the read saw one.</summary>
    public ChromeTab? SelectedTab => Tabs.FirstOrDefault(tab => tab.Selected);

    /// <summary>
    /// Windows in the order a person scans them: the current desktop first, then monitor by monitor (left to right),
    /// then top to bottom and left to right on each monitor. Unknown positions go last.
    /// </summary>
    public static IReadOnlyList<ChromeWindow> ScreenOrder(IEnumerable<ChromeWindow> windows, int? currentDesktop)
    {
        ArgumentNullException.ThrowIfNull(windows);
        const int far = 1 << 30;
        return
        [
            .. windows
                .OrderBy(window => !(window.DesktopNumber is { } desktop && desktop == currentDesktop))
                .ThenBy(window => window.DesktopNumber ?? far)
                .ThenBy(window => window.Monitor?.X ?? far)
                .ThenBy(window => window.Monitor?.Y ?? far)
                .ThenBy(window => window.Rect?.Top ?? far)
                .ThenBy(window => window.Rect?.Left ?? far),
        ];
    }
}
