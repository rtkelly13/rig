using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Core.Chrome.MacOS;

/// <summary>One tab in the native strip, with the group whose header precedes it.</summary>
/// <param name="Node">The tab's element.</param>
/// <param name="Group">Its group; null when it is in none.</param>
public sealed record TabSlot(AxNode Node, TabGroup? Group);

/// <summary>The Chrome profile a window's avatar names.</summary>
/// <param name="Directory">The profile's directory under Chrome's user data.</param>
/// <param name="Name">The profile's display name.</param>
public sealed record ProfileMatch(string Directory, string Name);

/// <summary>
/// Pairs native accessibility evidence with the scripting snapshot. Scripting gives stable ids, titles and URLs;
/// accessibility gives what only the browser UI shows: pinned, audible, memory, group membership and the profile
/// avatar. The two are paired only one-to-one, and only when the tab strips agree title for title. Anything uncertain
/// leaves a tab pinned and audible with <see cref="MetadataUnavailable"/>, so no planner acts on it. Ported from
/// <c>desktop_manager.chrome.drivers.macos_read</c>.
/// </summary>
public static partial class MacRead
{
    /// <summary>The alert a tab carries when native metadata could not be tied to it.</summary>
    public const string MetadataUnavailable = "Accessibility metadata unavailable";

    private static readonly string[] CloseNames = ["Close", "Close tab"];

    // The app menu that follows the profile avatar in Chrome's toolbar.
    private static readonly string[] AppMenuNames = ["Customize and control Google Chrome", "Chrome"];

    /// <summary><paramref name="node"/> and every element below it, depth first, never entering a skipped role.</summary>
    public static IEnumerable<AxNode> Walk(AxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var pending = new Stack<AxNode>([node]);
        while (pending.TryPop(out var current))
        {
            yield return current;
            if (!AxReader.SkippedRoles.Contains(current.Role))
            {
                for (var index = current.Children.Count - 1; index >= 0; index--)
                {
                    pending.Push(current.Children[index]);
                }
            }
        }
    }

    /// <summary>The one profile this window's avatar names; null when it names none, or several.</summary>
    public static ProfileMatch? ProfileOf(AxNode? node, IReadOnlyDictionary<string, ProfileInfo> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (node is null)
        {
            return null;
        }

        // Chrome's toolbar places the avatar immediately before its app menu. Some macOS builds omit AXIdentifier, so
        // that structure is the evidence. These are particular elements, so they are held by reference.
        var toolbarAvatars = new HashSet<AxNode>(ReferenceEqualityComparer.Instance);
        foreach (var toolbar in Walk(node).Where(item => item.Role == "AXToolbar"))
        {
            foreach (var parent in Walk(toolbar))
            {
                for (var index = 1; index < parent.Children.Count; index++)
                {
                    if (AppMenuNames.Contains(parent.Children[index].Name, StringComparer.Ordinal))
                    {
                        _ = toolbarAvatars.Add(parent.Children[index - 1]);
                    }
                }
            }
        }

        var matches = new HashSet<ProfileMatch>();
        foreach (var item in Walk(node).Where(item => item.Role is "AXButton" or "AXPopUpButton"))
        {
            string[] labels = [item.Title, item.Description, item.Help];
            var identifier = item.Identifier.ToLowerInvariant();

            // Bare names count only on a control positively identified as the avatar.
            var avatar = identifier.Contains("avatar", StringComparison.Ordinal)
                || identifier.Contains("profile", StringComparison.Ordinal)
                || toolbarAvatars.Contains(item);
            foreach (var (directory, info) in profiles)
            {
                if (string.IsNullOrEmpty(info.Name))
                {
                    continue;
                }

                var names = info.Aliases();
                var explicitLabels = names.SelectMany(alias => new[] { $"Profile: {alias}", $"Profile {alias}" }).ToHashSet(StringComparer.Ordinal);

                // A signed-in avatar reads "<account name> (<profile name>)".
                var badged = names.Concat(names.Select(alias => $"{alias} ({info.Name})")).ToHashSet(StringComparer.Ordinal);
                if (labels.Any(label => explicitLabels.Contains(label) || (avatar && badged.Contains(label))))
                {
                    _ = matches.Add(new ProfileMatch(directory, info.Name));
                }
            }
        }

        return matches.Count == 1 ? matches.Single() : null;
    }

    /// <summary>
    /// The tab slots in <paramref name="node"/>'s strip, each group header preceding its sibling tabs. Chromium exposes
    /// both the tab container and a group header as AXTabGroup; only an expanded header with a complete run of tabs
    /// after it establishes membership. Anything uncertain, and any one element read twice with different evidence,
    /// returns no slots at all.
    /// </summary>
    public static IReadOnlyList<TabSlot> TabSlots(AxNode node, long windowId)
    {
        ArgumentNullException.ThrowIfNull(node);
        var result = new List<TabSlot>();
        if (!Visit(node, windowId, result))
        {
            return [];
        }

        // Native accessibility is a graph: one tab can be reached through several parents. Only identical nonzero
        // identities collapse; conflicting evidence fails closed.
        var unique = new List<TabSlot>();
        var seen = new Dictionary<long, TabSlot>();
        foreach (var slot in result)
        {
            var identity = slot.Node.Identity;
            if (identity != 0 && seen.TryGetValue(identity, out var earlier))
            {
                if (earlier != slot)
                {
                    return [];
                }

                continue;
            }

            unique.Add(slot);
            if (identity != 0)
            {
                seen[identity] = slot;
            }
        }

        return unique;
    }

    /// <summary>
    /// Pinned tabs lead the strip and never show a close control; a label breaking either rule means the names cannot
    /// be trusted for pinning.
    /// </summary>
    public static bool PinnedLabelsConsistent(IReadOnlyList<AxNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var pinned = nodes.Select(node => TabNames.Parse(node.Name).Pinned).ToList();
        if (pinned.SkipWhile(flag => flag).Any(flag => flag))
        {
            return false;
        }

        return !nodes.Where((node, index) => pinned[index] && ShowsClose(node)).Any();
    }

    /// <summary>Whether <paramref name="node"/> is <paramref name="raw"/>'s window by title: scripting abbreviates it, accessibility shows the active page's.</summary>
    public static bool MatchesWindowTitle(AxNode node, JxaWindow raw)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(raw);
        if (node.Title == raw.Title)
        {
            return true;
        }

        if (raw.ActiveTab is not { } active)
        {
            return false;
        }

        var full = active.Title + " - Google Chrome";
        return node.Title == full || node.Title.StartsWith(full + " – ", StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="node"/>'s tab strip repeats <paramref name="raw"/>'s titles and selection, in order.</summary>
    public static bool StripMatches(AxNode node, JxaWindow raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return Agrees(TabSlots(node, raw.Id), raw);
    }

    /// <summary>
    /// The one accessibility window for <paramref name="raw"/>, by title, else by tab strip, since Chrome leaves the
    /// title empty on some pages (a New Tab page). Either way the pairing must be one-to-one against every window in
    /// <paramref name="records"/>; otherwise null.
    /// </summary>
    public static AxNode? WindowNode(IEnumerable<AxNode> axWindows, JxaWindow raw, IReadOnlyList<JxaWindow> records)
    {
        ArgumentNullException.ThrowIfNull(axWindows);
        ArgumentNullException.ThrowIfNull(records);
        var candidates = axWindows.ToList();
        foreach (var matches in new Func<AxNode, JxaWindow, bool>[] { MatchesWindowTitle, StripMatches })
        {
            var nodes = candidates.Where(node => matches(node, raw)).ToList();
            if (nodes.Count > 0)
            {
                var unique = nodes.Count == 1 && records.Count(other => matches(nodes[0], other)) == 1;
                return unique ? nodes[0] : null;
            }
        }

        return null;
    }

    /// <summary>Each scripting tab's native slot, in order; null when there is no window or the strips disagree.</summary>
    public static IReadOnlyList<TabSlot>? AlignedSlots(AxNode? node, JxaWindow raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (node is null)
        {
            return null;
        }

        var slots = TabSlots(node, raw.Id);
        return Agrees(slots, raw) && PinnedLabelsConsistent([.. slots.Select(slot => slot.Node)]) ? slots : null;
    }

    /// <summary>
    /// Chrome's windows from a settled read: the scripting <paramref name="snapshot"/>, the accessibility windows read
    /// while it held (none when accessibility was unavailable), and the profiles from <c>Local State</c>. A window whose
    /// profile is unclear, or which is not a normal window, gets a scope of its own. A duplicate id or an active tab
    /// past the strip throws <see cref="ChromeMacException"/>. The pure half of Python's <c>ChromeMac.list_windows</c>.
    /// </summary>
    public static IReadOnlyList<ChromeWindow> Windows(JxaSnapshot snapshot, IReadOnlyList<AxNode> axWindows, IReadOnlyDictionary<string, ProfileInfo> profiles)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var windows = new List<ChromeWindow>();
        var seenWindows = new HashSet<long>();
        var seenTabs = new HashSet<long>();
        foreach (var raw in snapshot.Windows)
        {
            if (!seenWindows.Add(raw.Id))
            {
                throw new ChromeMacException("Duplicate Chrome window ID");
            }

            if (raw.Active > raw.Tabs.Count)
            {
                throw new ChromeMacException("Invalid Chrome window state");
            }

            // Duplicate titles or strips cannot identify a window safely, and reordered or hidden tabs make positional
            // matching unsafe; either way the tabs go without native evidence.
            var node = WindowNode(axWindows, raw, snapshot.Windows);
            var slots = AlignedSlots(node, raw);
            var tabs = new List<ChromeTab>();
            for (var index = 0; index < raw.Tabs.Count; index++)
            {
                if (!seenTabs.Add(raw.Tabs[index].Id))
                {
                    throw new ChromeMacException("Duplicate Chrome tab ID");
                }

                tabs.Add(Tab(raw.Tabs[index], index, raw, slots?[index]));
            }

            var profile = ProfileOf(node, profiles);
            var unknown = profile is null || raw.Mode != "normal";
            windows.Add(new ChromeWindow(raw.Id, raw.Title, profile?.Directory, profile?.Name, raw.Id, raw.Minimized, null, tabs)
            {
                Rect = Rect(raw.Bounds),
                ProfileScope = unknown ? string.Create(CultureInfo.InvariantCulture, $"(unknown profile: window {raw.Id})") : null,
            });
        }

        return windows;
    }

    // Bounds as edges; null when there are none, or when an edge lies past what a pixel coordinate holds.
    private static WindowRect? Rect(JxaBounds? bounds)
    {
        if (bounds is null)
        {
            return null;
        }

        var right = (long)bounds.X + bounds.Width;
        var bottom = (long)bounds.Y + bounds.Height;
        return right is < int.MinValue or > int.MaxValue || bottom is < int.MinValue or > int.MaxValue
            ? null
            : new WindowRect(bounds.X, bounds.Y, (int)right, (int)bottom);
    }

    /// <summary>
    /// A scripting tab with its native evidence. Without evidence it is pinned and audible with
    /// <see cref="MetadataUnavailable"/>, so nothing acts on it. <see cref="ChromeTab.Url"/> is set only when the
    /// address parses as an absolute URI; its <see cref="Uri.OriginalString"/> is then the exact text Chrome reported.
    /// A tab id past <see cref="int.MaxValue"/> does not fit a <see cref="RuntimeId"/> and throws
    /// <see cref="ChromeMacException"/>.
    /// </summary>
    public static ChromeTab Tab(JxaTab raw, int index, JxaWindow window, TabSlot? slot)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(window);
        if (raw.Id > int.MaxValue)
        {
            throw new ChromeMacException("Invalid Chrome tab identity");
        }

        var node = slot?.Node;
        var group = slot?.Group;
        var parsed = node is null ? null : TabNames.Parse(node.Name);

        // Chrome labels pinned tabs "– Pinned". Inactive tabs hide their close control, so its absence is normal;
        // PinnedLabelsConsistent rejects labels that contradict the pinned-first order.
        var groupKnown = node is not null && (group is not null || !GroupSuffix().IsMatch(node.Name));
        var known = parsed is not null && parsed.Title == raw.Title && groupKnown;
        return new ChromeTab(
            new RuntimeId([(int)raw.Id]),
            window.Id,
            index,
            node?.Name ?? raw.Title,
            raw.Title,
            index + 1 == window.Active,
            !known || parsed!.Pinned,
            parsed?.Loaded ?? false,
            !known || parsed!.Audible)
        {
            Alerts = known ? parsed!.Alerts : [MetadataUnavailable],
            MemoryMb = parsed?.MemoryMb,
            Group = group,
            Url = Uri.TryCreate(raw.Address, UriKind.Absolute, out var url) ? url : null,
        };
    }

    /// <summary>
    /// A group's identity: the window's scripting id and the header element's CFHash, each split into two 32-bit
    /// halves so neither is truncated, then -1 as Python's tuple ended.
    /// </summary>
    public static RuntimeId GroupId(long windowId, long header) =>
        new([(int)(windowId >> 32), unchecked((int)windowId), (int)(header >> 32), unchecked((int)header), -1]);

    private static bool IsTab(AxNode item) =>
        item.Role == "AXRadioButton" && (item.Subrole == "AXTabButton" || item.Description == "tab");

    private static bool ShowsClose(AxNode node) =>
        Walk(node).Any(child => child.Role == "AXButton" && CloseNames.Contains(child.Name, StringComparer.Ordinal));

    private static bool Visit(AxNode item, long windowId, List<TabSlot> result)
    {
        if (AxReader.SkippedRoles.Contains(item.Role))
        {
            return true;
        }

        if (IsTab(item))
        {
            result.Add(new TabSlot(item, null));
            return true;
        }

        var index = 0;
        while (index < item.Children.Count)
        {
            var child = item.Children[index];
            if (TabNames.ParseGroupHeader(child.Name) is not { } title)
            {
                if (!Visit(child, windowId, result))
                {
                    return false;
                }

                index++;
                continue;
            }

            // Titles and tab previews can hold a count phrase of their own; without one unambiguous count, none is safe.
            var counts = GroupCount().Matches(child.Name);
            var expanded = child.Expanded == true || child.Name.EndsWith(" – Expanded", StringComparison.Ordinal);
            if (item.Role != "AXTabGroup"
                || child.Role != "AXTabGroup"
                || child.Expanded == false
                || child.Name.EndsWith(" – Collapsed", StringComparison.Ordinal)
                || !expanded
                || counts.Count != 1)
            {
                return false;
            }

            // A count too large for an int cannot be followed by that many tabs, so it fails as a short run does.
            if (!int.TryParse(counts[0].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                return false;
            }

            var members = item.Children.Skip(index + 1).Take(count).ToList();
            if (count == 0 || members.Count != count || !members.All(IsTab))
            {
                return false;
            }

            var group = new TabGroup(GroupId(windowId, child.Identity), title);
            result.AddRange(members.Select(member => new TabSlot(member, group)));
            index += count + 1;
        }

        return true;
    }

    private static bool Agrees(IReadOnlyList<TabSlot> slots, JxaWindow raw) =>
        slots.Count == raw.Tabs.Count
        && slots.Select((slot, index) => (slot, index)).All(pair =>
            TabNames.Parse(pair.slot.Node.Name).Title == raw.Tabs[pair.index].Title
            && pair.slot.Node.Selected == (pair.index + 1 == raw.Active));

    [GeneratedRegex(@" – (\d+) [Tt]abs?\b")]
    private static partial Regex GroupCount();

    [GeneratedRegex(@" [-–] Part of (?:unnamed group|group )")]
    private static partial Regex GroupSuffix();
}
