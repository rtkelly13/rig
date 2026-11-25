using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Rig.Core.Text;

namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// Chrome on macOS: scripting for identity and closing, accessibility for metadata and menus. Chrome's scripting
/// dictionary supplies stable window and tab ids, titles and URLs, and can select and close a tab without focus;
/// native accessibility supplies what only the browser UI shows. Missing or ambiguous evidence leaves the affected
/// tabs pinned and audible, so no planner acts on them. Construction performs no OS calls.
/// </summary>
/// <remarks>
/// Every action re-finds its target and checks it before acting, and reports what happened rather than throwing on a
/// refusal. The native UI is scoped to one action and released when the action ends.
/// </remarks>
public sealed partial class ChromeMac : IChromeWindowReader, IChromeTabActions, IChromeGroupActions, IChromeSavedGroupActions
{
    /// <summary>Why a trusted read found nothing: macOS accessibility lists only the current Space's windows.</summary>
    public const string NoAxWindows = "accessibility sees none of Chrome's windows (another Space or a full-screen app is in front)";

    private static bool UnknownPrPage(ChromeTab tab) =>
        !tab.Selected && tab.Url is { } url && PrPrune.RefFromUrl(url) is { } pr && PrPrune.RefFromTitle(tab.Title) == pr;

    /// <summary>Reads of one window before the accessibility evidence is dropped.</summary>
    public const int ReadAttempts = 4;

    /// <summary>The pause between looks while Chrome settles.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The default wait for one step of an action.</summary>
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The default wait for a window to come to the front.</summary>
    public static readonly TimeSpan ForegroundTimeout = TimeSpan.FromSeconds(2);

    private const string Failed = "Timed out waiting for Chrome UI confirmation";

    private static readonly string[] FlatGroupNames = ["Add Tab to New Group", "Add tab to new group"];
    private static readonly string[] GroupNames = ["Add Tab to Group", "Add tab to group"];
    private static readonly string[] NewGroupNames = ["New Group", "New group"];
    private static readonly string[] MoveNames = ["Move Tab to Another Window", "Move tab to another window"];
    private static readonly string[] TitleFieldNames = ["Tab group title", "Tab-group title"];
    private static readonly string[] UngroupDialogNames = ["Ungroup Tab Group?", "Ungroup tab group?"];

    private readonly IChromeJxa _jxa;
    private readonly Func<IMacUiApi> _api;
    private readonly Func<IReadOnlyDictionary<string, ProfileInfo>> _profiles;
    private readonly TimeProvider _time;
    private readonly Action<TimeSpan> _sleep;

    /// <summary>Creates the driver over its scripting bridge, accessibility session and profile reader.</summary>
    public ChromeMac(
        IChromeJxa jxa,
        Func<IMacUiApi> api,
        Func<IReadOnlyDictionary<string, ProfileInfo>> profiles,
        TimeProvider? time = null,
        Action<TimeSpan>? sleep = null)
    {
        _jxa = jxa ?? throw new ArgumentNullException(nameof(jxa));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _time = time ?? TimeProvider.System;
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>Chrome's windows and tabs on every Space; empty when Chrome is not running.</summary>
    public IReadOnlyList<ChromeWindow> ListWindows()
    {
        var (snapshot, axWindows, problem) = BracketedRead();
        return snapshot.Windows.Count == 0 ? [] : MacRead.Windows(snapshot, axWindows, _profiles(), problem);
    }

    /// <summary>Chrome's front window when Chrome is the active application, else 0; failures propagate.</summary>
    public long ForegroundWindow()
    {
        var current = _jxa.Snapshot();
        if (!current.Frontmost)
        {
            return 0;
        }

        var front = current.FrontWindow();
        return front?.Id ?? throw new ChromeMacException("Cannot identify Chrome's foreground window");
    }

    /// <inheritdoc/>
    public CloseOutcome CloseTab(ChromeTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var started = _time.GetTimestamp();
        CloseOutcome Outcome(bool closed, string detail) => new(closed, detail, _time.GetElapsedTime(started));
        try
        {
            _ = ActionIds.Of(tab);
            if (tab.Url is null)
            {
                return Outcome(false, "URL unavailable; refusing close");
            }

            var matches = ListWindows()
                .Where(window => window.Handle == tab.WindowHandle)
                .SelectMany(window => window.Tabs)
                .Where(current => current.RuntimeId.Equals(tab.RuntimeId))
                .ToList();
            if (matches.Count != 1)
            {
                return Outcome(false, "tab no longer present");
            }

            var current = matches[0];
            if (current.Title != tab.Title)
            {
                return Outcome(false, $"title changed to {QuotedText.Str(current.Title)}");
            }

            if (current.Url?.OriginalString != tab.Url.OriginalString)
            {
                return Outcome(false, "URL changed; refusing close");
            }

            // Without native evidence the pinned and audible flags are placeholders. Only a PR page the planner can
            // vouch for (its address and title name the same PR) that is not in front in its window may close then.
            if (current.EvidenceUnknown && !UnknownPrPage(current))
            {
                return Outcome(false, $"{MacRead.MetadataUnavailable}; refusing close");
            }

            if (!current.EvidenceUnknown && (current.Pinned || current.Audible))
            {
                return Outcome(false, "tab is pinned or audible; refusing close");
            }

            var reply = _jxa.Act(JxaScripts.TabAction(tab, JxaScripts.CloseAction(tab.Title, tab.Url.OriginalString)));
            if (reply != "invoked")
            {
                return Outcome(false, $"close refused: {reply}");
            }

            var gone = TryPoll(
                () => _jxa.Snapshot().Windows.SelectMany(window => window.Tabs).Any(raw => raw.Id == tab.RuntimeId.Parts[0]) ? null : "gone",
                StepTimeout,
                out _);
            return Outcome(gone, gone ? "closed" : "invoked close but the tab is still present; a prompt may be open");
        }
        catch (ChromeMacException error)
        {
            return Outcome(false, $"close failed: {error.Message}");
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<long, ChromeTab?> RestoreSelection(
        IEnumerable<ChromeWindow> before,
        IReadOnlyDictionary<long, ChromeTab>? fallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        var restored = new Dictionary<long, ChromeTab?>();
        foreach (var window in before)
        {
            ChromeTab? chosen = null;
            foreach (var candidate in new[] { window.SelectedTab, fallbacks?.GetValueOrDefault(window.Handle) })
            {
                if (candidate is null || candidate.WindowHandle != window.Handle)
                {
                    continue;
                }

                string reply;
                try
                {
                    reply = _jxa.Act(JxaScripts.TabAction(candidate, JxaScripts.SelectAction));
                }
                catch (ChromeMacException)
                {
                    break;
                }

                if (reply == "selected")
                {
                    chosen = candidate;
                    break;
                }

                if (reply != "missing")
                {
                    break;
                }
            }

            restored[window.Handle] = chosen;
        }

        return restored;
    }

    /// <inheritdoc/>
    public ForegroundLease Foreground(long handle, TimeSpan? timeout = null)
    {
        MacUi ui;
        try
        {
#pragma warning disable CA2000 // The lease's restore action owns the session and disposes it.
            ui = MacUi.Open(_api());
#pragma warning restore CA2000
        }
        catch (AccessibilityUnavailableException)
        {
            return new ForegroundLease(false);
        }

        var kept = false;
        try
        {
            var before = _jxa.Snapshot();
            var targets = before.Windows.Where(window => window.Id == handle).ToList();
            var front = before.FrontWindow();
            if (targets.Count != 1 || front is null)
            {
                return new ForegroundLease(false);
            }

            var cursor = ui.Cursor();
            var focus = ui.CaptureFocus(() => _jxa.FrontmostPid());

            void Restore()
            {
                try
                {
                    _ = _jxa.Act(JxaScripts.Restore(handle, front.Id, targets[0].Minimized));
                }
                finally
                {
                    try
                    {
                        ui.RestoreFocus(focus);
                    }
                    finally
                    {
                        try
                        {
                            ui.RestoreCursor(cursor);
                        }
                        finally
                        {
                            ui.Dispose();
                        }
                    }
                }
            }

            bool arrived;
            try
            {
                arrived = Activate(handle, timeout ?? ForegroundTimeout);
            }
            catch
            {
                Restore();
                kept = true;
                throw;
            }

            kept = true;
            return new ForegroundLease(arrived, Restore);
        }
        catch (Exception error) when (error is ChromeMacException or AccessibilityUnavailableException)
        {
            return new ForegroundLease(false);
        }
        finally
        {
            if (!kept)
            {
                ui.Dispose();
            }
        }
    }

    /// <summary>
    /// Brings <paramref name="handle"/> to the front and waits until it is Chrome's front window; false when it has
    /// not arrived by <paramref name="timeout"/>.
    /// </summary>
    public bool Activate(long handle, TimeSpan timeout)
    {
        if (_jxa.Act(JxaScripts.Activate(handle)) != "invoked")
        {
            return false;
        }

        return TryPoll(() => ForegroundWindow() == handle ? "front" : null, timeout, out _);
    }

    /// <inheritdoc/>
    public GroupOutcome GroupTab(ChromeTab tab, string title, bool isNew, long? homeHandle = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(title);
        var started = _time.GetTimestamp();
        GroupOutcome Outcome(bool grouped, string detail) => new(grouped, detail, _time.GetElapsedTime(started));
        try
        {
            if (title.Length == 0 || title.Any(character => character < 32) || GroupCountPhrase().IsMatch(title))
            {
                throw new ChromeMacException("Group title is empty or cannot be read unambiguously");
            }

            if (tab.Group is not null)
            {
                throw new ChromeMacException("Tab is already grouped");
            }

            var home = homeHandle ?? tab.WindowHandle;
            var wait = timeout ?? StepTimeout;
            return WithAction(tab, wait, action =>
            {
                var live = action.Live;
                TabGroup? target = null;
                if (isNew)
                {
                    if (home != tab.WindowHandle || Groups(live.Windows, live.Window.ProfileDir ?? "", title).Count > 0)
                    {
                        throw new ChromeMacException("New group needs its home window and a unique title");
                    }
                }
                else
                {
                    target = TargetGroup(live.Windows, live.Window, title, home);
                }

                var members = live.Windows
                    .SelectMany(window => window.Tabs)
                    .Where(member => target is not null && member.Group == target)
                    .ToList();
                if (home != tab.WindowHandle)
                {
                    action.Watch(home);
                }

                action.Open(live.Node);
                var entry = Entry(action.Ui, live.Pid, [.. FlatGroupNames, .. GroupNames], wait);
                AxNode chosen;
                if (FlatGroupNames.Contains(entry.Name, StringComparer.Ordinal))
                {
                    if (!isNew)
                    {
                        throw new ChromeMacException("Target group no longer offered");
                    }

                    chosen = entry;
                }
                else
                {
                    action.Press(entry);
                    var menu = Submenu(action.Ui, live.Pid, GroupNames, wait);
                    var names = isNew ? NewGroupNames : [title];
                    chosen = Unique([.. menu.Children.Where(node => node.Role == "AXMenuItem" && names.Contains(node.Name, StringComparer.Ordinal))], "Target group menu entry missing or ambiguous");
                }

                action.Unchanged();
                action.Press(chosen);
                if (isNew)
                {
                    var bubble = Bubble(action.Ui, live.Pid, wait);
                    var field = Unique([.. MacRead.Walk(bubble).Where(node => node.Role == "AXTextField" && TitleFieldNames.Contains(node.Name, StringComparer.Ordinal))], "Group title field is ambiguous");
                    ForegroundOrThrow(live.Window.Handle);
                    if (!action.Ui.Focused(field))
                    {
                        throw new ChromeMacException("Group title field lost focus");
                    }

                    var unnamed = Poll(() => Arrival(tab, home, live.Window.ProfileDir ?? "", ""), wait);
                    action.Ui.SetText(field, title, () => ForegroundOrThrow(tab.WindowHandle));
                    ForegroundOrThrow(tab.WindowHandle);
                    if (!action.Ui.Focused(field))
                    {
                        throw new ChromeMacException("Group title field lost focus");
                    }

                    action.Ui.Key(36); // Return
                    if (unnamed.Group is null)
                    {
                        throw new ChromeMacException("New group identity could not be established");
                    }
                }

                _ = Poll(() => Arrival(tab, home, live.Window.ProfileDir ?? "", title, target, members), wait);
                return true;
            })
                ? Outcome(true, isNew ? "created" : "joined")
                : Outcome(false, "grouping failed");
        }
        catch (Exception error) when (error is ChromeMacException or AccessibilityUnavailableException)
        {
            return Outcome(false, error.Message);
        }
    }

    /// <inheritdoc/>
    public GroupOutcome Ungroup(ChromeTab tab, string title, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(title);
        var started = _time.GetTimestamp();
        GroupOutcome Outcome(bool grouped, string detail) => new(grouped, detail, _time.GetElapsedTime(started));
        try
        {
            if (tab.Group is null || tab.Group.Title != title)
            {
                throw new ChromeMacException("Tab is no longer in the requested group");
            }

            var wait = timeout ?? StepTimeout;
            return WithAction(tab, wait, action =>
            {
                var live = action.Live;
                if (TargetGroup([live.Window], live.Window, title, tab.WindowHandle) != tab.Group)
                {
                    throw new ChromeMacException("Group identity changed");
                }

                var members = live.Window.Tabs.Where(member => member.Group == tab.Group).ToList();
                foreach (var member in members)
                {
                    Safe(member);
                }

                // One header can be reached through several paths of the AX graph.
                var header = Unique(
                    [.. MacRead.Walk(live.WindowNode)
                        .Where(node => node.Role == "AXTabGroup"
                            && TabNames.ParseGroupHeader(node.Name) == title
                            && node.Identity == HeaderIdentity(tab.Group))
                        .DistinctBy(node => node.Identity)],
                    "Native group header missing or ambiguous");
                action.Open(header);
                var bubble = Bubble(action.Ui, live.Pid, wait);
                var chosen = Unique([.. MacRead.Walk(bubble).Where(node => node.Role == "AXButton" && node.Name == "Ungroup")], "Group editor Ungroup button missing or ambiguous");
                action.Unchanged();
                action.Press(chosen);
                var confirmed = false;

                string? Finished()
                {
                    var windows = ListWindows();
                    var window = Window(windows, tab.WindowHandle);
                    Known(window);
                    if (window.ProfileDir != live.Window.ProfileDir)
                    {
                        throw new ChromeMacException("Group window profile changed");
                    }

                    var remaining = new List<ChromeTab>();
                    foreach (var member in members)
                    {
                        var current = Unique([.. window.Tabs.Where(other => other.RuntimeId.Equals(member.RuntimeId))], "Ungrouped member missing or ambiguous");
                        Safe(current);
                        if (TabContent.Of(current) != TabContent.Of(member))
                        {
                            throw new ChromeMacException("Group member content changed");
                        }

                        if (current.Group is not null)
                        {
                            if (current.Group != tab.Group)
                            {
                                throw new ChromeMacException("Group member moved to another group during confirmation");
                            }

                            remaining.Add(current);
                        }
                    }

                    if (remaining.Count == 0)
                    {
                        return "ungrouped";
                    }

                    if (remaining.Count != members.Count
                        || !window.Tabs.Where(other => other.Group == tab.Group).Select(other => other.RuntimeId).ToHashSet().SetEquals(members.Select(member => member.RuntimeId)))
                    {
                        throw new ChromeMacException("Group membership changed during confirmation");
                    }

                    var tree = action.Ui.ApplicationTree(live.Pid);
                    var dialogs = Innermost(MacRead.Walk(tree).Where(node =>
                        Dialog(node)
                        && (UngroupDialogNames.Contains(node.Name, StringComparer.Ordinal)
                            || MacRead.Walk(node).Any(child => UngroupDialogNames.Contains(child.Name, StringComparer.Ordinal)))));
                    if (dialogs.Count > 0 && !confirmed)
                    {
                        var dialog = Unique(dialogs, "Ungroup confirmation dialog is ambiguous");
                        var button = Unique([.. MacRead.Walk(dialog).Where(node => node.Role == "AXButton" && node.Name == "Ungroup")], "Ungroup confirmation button is ambiguous");
                        action.Press(button);
                        confirmed = true;
                    }

                    return null;
                }

                _ = Poll(Finished, wait);
                return true;
            })
                ? Outcome(true, "ungrouped")
                : Outcome(false, "ungrouping failed");
        }
        catch (Exception error) when (error is ChromeMacException or AccessibilityUnavailableException)
        {
            return Outcome(false, error.Message);
        }
    }

    /// <inheritdoc/>
    public MoveOutcome MoveToWindow(ChromeTab tab, long targetHandle, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var started = _time.GetTimestamp();
        MoveOutcome Outcome(bool moved, string detail, ChromeTab? arrived) => new(
            new GroupOutcome(moved, detail, _time.GetElapsedTime(started)), arrived);
        try
        {
            if (tab.Group is not null || targetHandle == tab.WindowHandle)
            {
                throw new ChromeMacException("Move requires an ungrouped tab and a different window");
            }

            var wait = timeout ?? StepTimeout;
            return WithAction(tab, wait, action =>
            {
                var live = action.Live;
                var target = Window(live.Windows, targetHandle);
                Known(target);
                if (target.ProfileDir != live.Window.ProfileDir)
                {
                    throw new ChromeMacException("Cannot move tabs across profiles");
                }

                action.Open(live.Node);
                var entry = Entry(action.Ui, live.Pid, MoveNames, wait);
                action.Press(entry);
                var menu = Submenu(action.Ui, live.Pid, MoveNames, wait);
                var chosen = Unique([.. menu.Children.Where(node => node.Role == "AXMenuItem" && NamesWindow(node.Name, target))], "Target window menu entry missing or ambiguous");
                var peers = live.Windows
                    .Where(window => window.Handle != tab.WindowHandle
                        && window.ProfileDir == live.Window.ProfileDir
                        && NamesWindow(chosen.Name, window))
                    .ToList();
                if (peers.Count != 1 || peers[0].Handle != targetHandle)
                {
                    throw new ChromeMacException("Target window label identifies more than one window");
                }

                action.Unchanged();
                action.Press(chosen);
                var moved = Poll(() => Arrival(tab, targetHandle, target.ProfileDir ?? "", null), wait);
                return new MoveOutcome(new GroupOutcome(true, "moved", _time.GetElapsedTime(started)), moved);
            });
        }
        catch (Exception error) when (error is ChromeMacException or AccessibilityUnavailableException)
        {
            return Outcome(false, error.Message, null);
        }
    }

    /// <summary>No saved-group reader on macOS yet: report none, so consolidation leaves them alone.</summary>
    public IReadOnlyList<SavedGroup> SavedGroups(long handle, string profile)
    {
        _ = handle;
        _ = profile;
        return [];
    }

    /// <summary>Saved-group deletion is not supported on macOS.</summary>
    public SavedDeleteOutcome DeleteSavedGroup(long handle, string profile, string title, TimeSpan? timeout = null)
    {
        _ = handle;
        _ = profile;
        _ = timeout;
        return new SavedDeleteOutcome(false, $"saved group {QuotedText.Str(title)}: not supported on macOS", [], TimeSpan.Zero);
    }

    // A snapshot and the AX windows read while it held. A second snapshot brackets the AX read: stable ids and
    // ordering let identical titles map positionally without confusing two tabs. A live page can retitle mid-read, so
    // an unsettled read is retried a few times before the AX evidence is dropped and every tab is left protected.
    // Without AX windows, the third value says why.
    private (JxaSnapshot Snapshot, IReadOnlyList<AxNode> AxWindows, string? Problem) BracketedRead()
    {
        var current = _jxa.Snapshot();
        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            if (current.Windows.Count == 0)
            {
                return (current, [], null);
            }

            IReadOnlyList<AxNode> axWindows;
            try
            {
                axWindows = new AxReader(_api()).Windows(current.Pid);
            }
            catch (AccessibilityUnavailableException error)
            {
                return (current, [], error.Message);
            }

            var after = _jxa.Snapshot();
            if (SnapshotsEqual(after, current))
            {
                return (current, axWindows, axWindows.Count == 0 ? NoAxWindows : null);
            }

            current = after;
            _sleep(PollInterval);
        }

        return (current, [], "Chrome's tabs kept changing during the read");
    }

    private Live ReadLive(MacUi ui, ChromeTab expected)
    {
        var windows = ListWindows();
        var located = Current(windows, expected);
        var window = located.Window;
        var current = located.Tab;
        var snapshot = _jxa.Snapshot();
        var raw = Unique([.. snapshot.Windows.Where(candidate => candidate.Id == window.Handle)], "Native window missing or ambiguous");
        var axWindows = ui.BrowserWindows(snapshot.Pid);
        if (!SnapshotsEqual(snapshot, _jxa.Snapshot()))
        {
            throw new ChromeMacException("Chrome changed during native UI validation");
        }

        var found = MacRead.WindowNode(axWindows, raw, snapshot.Windows);
        if (found is null)
        {
            throw new ChromeMacException("AX window missing or ambiguous");
        }

        if (windows.Count(other => other.Title == window.Title) != 1)
        {
            throw new ChromeMacException("Browser window title is ambiguous");
        }

        var profile = MacRead.ProfileOf(found, _profiles());
        if ((profile?.Directory ?? null) != window.ProfileDir)
        {
            throw new ChromeMacException("AX window profile changed or is ambiguous");
        }

        var slots = MacRead.TabSlots(found, window.Handle);
        if (slots.Count != raw.Tabs.Count || raw.Tabs.Count != window.Tabs.Count)
        {
            throw new ChromeMacException("Native tab strip is incomplete");
        }

        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            var record = raw.Tabs[index];
            var tab = window.Tabs[index];
            if (record.Id != tab.RuntimeId.Parts[0]
                || record.Title != tab.Title
                || record.Address != tab.Url?.OriginalString
                || TabNames.Parse(slot.Node.Name).Title != tab.Title
                || slot.Node.Selected != (index + 1 == raw.Active))
            {
                throw new ChromeMacException("Native tab strip no longer matches stable Chrome identities");
            }
        }

        var selected = slots[current.Index];
        Safe(MacRead.Tab(raw.Tabs[current.Index], current.Index, raw, selected));
        if (selected.Group != current.Group)
        {
            throw new ChromeMacException("Native group membership changed");
        }

        return new Live(windows, window, current, selected.Node, found, snapshot.Pid, raw, slots);
    }

    private T WithAction<T>(ChromeTab tab, TimeSpan timeout, Func<NativeAction, T> body)
    {
        using var ui = MacUi.Open(_api());
        var cursor = ui.Cursor();
        NativeAction? action = null;
        try
        {
            var live = ReadLive(ui, tab);
            if (!Activate(tab.WindowHandle, timeout))
            {
                throw new ChromeMacException("Chrome window would not come to the front");
            }

            // Activation can change layout and selection; re-query retained UI.
            var refreshed = ReadLive(ui, tab);
            if (refreshed.Pid != live.Pid)
            {
                throw new ChromeMacException("Chrome process changed during activation");
            }

            action = new NativeAction(this, ui, refreshed);
            return body(action);
        }
        catch (Exception error) when (error is ChromeMacException or AccessibilityUnavailableException)
        {
            if (action is { Opened: true })
            {
                try
                {
                    ui.Escape(action.Live.Pid);
                }
                catch (Exception inner) when (inner is ChromeMacException or AccessibilityUnavailableException)
                {
                    // Dismissing is best effort; the original failure is what matters.
                }
            }

            throw;
        }
        finally
        {
            ui.RestoreCursor(cursor);
        }
    }

    private ChromeTab? Arrival(
        ChromeTab original,
        long handle,
        string profile,
        string? title,
        TabGroup? group = null,
        IReadOnlyList<ChromeTab>? members = null)
    {
        var windows = ListWindows();
        var found = windows.SelectMany(window => window.Tabs).Where(tab => tab.RuntimeId.Equals(original.RuntimeId)).ToList();
        var tab = found.Count > 0 ? Unique(found, "Moved tab missing or duplicated") : null;
        if (tab is null || tab.WindowHandle != handle)
        {
            return null;
        }

        var window = Window(windows, handle);
        // Chrome is still redrawing the strip right after an action, so a read that could not pair native evidence
        // with scripting is "not yet", not a refusal. The poll's timeout still bounds the wait.
        if (window.ProfileScope is not null || tab.Alerts.Contains(MacRead.MetadataUnavailable))
        {
            return null;
        }

        Known(window);
        if (window.ProfileDir != profile
            || tab.Title != original.Title
            || tab.Url?.OriginalString != original.Url?.OriginalString)
        {
            throw new ChromeMacException("Arriving tab identity, content or profile changed");
        }

        Safe(tab);
        if (title is null)
        {
            return tab.Group is null ? tab : null;
        }

        if (tab.Group is null || tab.Group.Title != title || (group is not null && tab.Group != group))
        {
            return null;
        }

        var grouped = window.Tabs.Where(member => member.Group == tab.Group).Select(TabContent.Of).ToHashSet();
        var expected = (members ?? []).Append(original).Select(TabContent.Of).ToHashSet();
        if (!grouped.SetEquals(expected))
        {
            throw new ChromeMacException("Resulting group membership differs from the requested tabs");
        }

        return tab;
    }

    private static Located Current(IReadOnlyList<ChromeWindow> windows, ChromeTab expected)
    {
        _ = ActionIds.Of(expected);
        Safe(expected);
        var source = Window(windows, expected.WindowHandle);
        Known(source);
        var current = Unique([.. source.Tabs.Where(tab => tab.RuntimeId.Equals(expected.RuntimeId))], "Tab missing or ambiguous");
        Safe(current);
        if (current.Title != expected.Title || current.Url?.OriginalString != expected.Url?.OriginalString)
        {
            throw new ChromeMacException("Tab title or URL changed; refusing action");
        }

        if (current.Group != expected.Group)
        {
            throw new ChromeMacException("Tab group changed; refusing action");
        }

        return new Located(source, current);
    }

    private static List<PlacedGroup> Groups(IReadOnlyList<ChromeWindow> windows, string profile, string title)
    {
        var groups = new Dictionary<TabGroup, PlacedGroup>();
        foreach (var window in windows)
        {
            if (window.ProfileDir != profile)
            {
                continue;
            }

            Known(window);
            if (window.Tabs.Any(tab => tab.Alerts.Contains(MacRead.MetadataUnavailable)))
            {
                throw new ChromeMacException("Profile group membership is incomplete");
            }

            foreach (var tab in window.Tabs)
            {
                if (tab.Group is not null && tab.Group.Title == title)
                {
                    groups[tab.Group] = new PlacedGroup(window.Handle, tab.Group);
                }
            }
        }

        return [.. groups.Values];
    }

    private static TabGroup TargetGroup(IReadOnlyList<ChromeWindow> windows, ChromeWindow source, string title, long? home)
    {
        var placed = Unique(Groups(windows, source.ProfileDir ?? "", title), "Target group missing or ambiguous");
        if (placed.Handle != (home ?? source.Handle))
        {
            throw new ChromeMacException("Target group is in a different window");
        }

        var target = Window(windows, placed.Handle);
        Known(target);
        if (target.ProfileDir != source.ProfileDir)
        {
            throw new ChromeMacException("Target group belongs to a different profile");
        }

        return placed.Group;
    }

    private void ForegroundOrThrow(long handle)
    {
        if (ForegroundWindow() != handle)
        {
            throw new ChromeMacException("Chrome window lost foreground focus");
        }
    }

    private static bool SnapshotsEqual(JxaSnapshot left, JxaSnapshot right) =>
        left.Pid == right.Pid
        && left.Frontmost == right.Frontmost
        && left.Windows.Count == right.Windows.Count
        && left.Windows.Zip(right.Windows).All(pair => WindowsEqual(pair.First, pair.Second));

    private static bool WindowsEqual(JxaWindow left, JxaWindow right) =>
        left.Id == right.Id
        && left.Index == right.Index
        && left.Title == right.Title
        && left.Active == right.Active
        && left.Minimized == right.Minimized
        && left.Mode == right.Mode
        && left.Bounds == right.Bounds
        && left.Tabs.SequenceEqual(right.Tabs);

    // A group id is (window hi, window lo, header hi, header lo, -1); rejoin the header's two halves.
    private static long HeaderIdentity(TabGroup group) =>
        group.RuntimeId.Parts.Count > 3
            ? ((long)group.RuntimeId.Parts[2] << 32) | (uint)group.RuntimeId.Parts[3]
            : long.MinValue;

    private static void Safe(ChromeTab tab)
    {
        if (tab.Url is null || tab.Pinned || tab.Audible || tab.Alerts.Contains(MacRead.MetadataUnavailable))
        {
            throw new ChromeMacException("Tab URL or AX safety evidence unavailable, or tab is pinned/audible");
        }
    }

    private static void Known(ChromeWindow window)
    {
        if (string.IsNullOrEmpty(window.ProfileDir) || window.ProfileScope is not null)
        {
            throw new ChromeMacException("Window profile is unknown or private; refusing action");
        }
    }

    private static ChromeWindow Window(IReadOnlyList<ChromeWindow> windows, long handle) =>
        Unique([.. windows.Where(window => window.Handle == handle)], "Window missing or ambiguous");

    private static T Unique<T>(IReadOnlyList<T> items, string detail) =>
        items.Count == 1 ? items[0] : throw new ChromeMacException(detail);

    private static List<AxNode> Menus(AxNode tree)
    {
        if (tree.Role == "AXMenu")
        {
            return [tree];
        }

        if (tree.Role == "AXMenuBar")
        {
            return [];
        }

        return [.. tree.Children.SelectMany(Menus)];
    }

    private AxNode Entry(MacUi ui, int pid, IReadOnlyList<string> names, TimeSpan timeout) =>
        Poll(
            () =>
            {
                var menus = Menus(ui.ApplicationTree(pid));
                if (menus.Count == 0)
                {
                    return null;
                }

                var menu = Unique(menus, "More than one native context menu is open");
                var entries = menu.Children.Where(child => child.Role == "AXMenuItem" && names.Contains(child.Name, StringComparer.Ordinal)).ToList();
                return entries.Count > 0 ? Unique(entries, "Menu entry is ambiguous") : null;
            },
            timeout);

    private AxNode Submenu(MacUi ui, int pid, IReadOnlyList<string> names, TimeSpan timeout) =>
        Poll(
            () =>
            {
                var tree = ui.ApplicationTree(pid);
                var parents = MacRead.Walk(tree).Where(node => node.Role == "AXMenuItem" && names.Contains(node.Name, StringComparer.Ordinal)).ToList();
                if (parents.Count == 0)
                {
                    return null;
                }

                var parent = Unique(parents, "Native submenu parent is ambiguous");
                var menus = parent.Children.Where(child => child.Role == "AXMenu").ToList();
                return menus.Count > 0 ? Unique(menus, "Native submenu is ambiguous") : null;
            },
            timeout);

    // The open group editor: the one container holding the group-title field. It is a plain AXGroup reachable through
    // several paths of the AX graph, so repeated references collapse by identity, and page content is never walked.
    private AxNode Bubble(MacUi ui, int pid, TimeSpan timeout) =>
        Poll(
            () =>
            {
                var found = MacRead.Walk(ui.ApplicationTree(pid))
                    .Where(node => node.Children.Any(child => child.Role == "AXTextField" && TitleFieldNames.Contains(child.Name, StringComparer.Ordinal)))
                    .DistinctBy(node => node.Identity)
                    .ToList();
                return found.Count > 0 ? Unique(found, "Group editor is ambiguous") : null;
            },
            timeout);

    private T Poll<T>(Func<T?> query, TimeSpan timeout)
        where T : class =>
        TryPoll(query, timeout, out var found) ? found : throw new ChromeMacException(Failed);

    private bool TryPoll<T>(Func<T?> query, TimeSpan timeout, [NotNullWhen(true)] out T? found)
        where T : class
    {
        var started = _time.GetTimestamp();
        while ((found = query()) is null)
        {
            var remaining = timeout - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            _sleep(remaining < PollInterval ? remaining : PollInterval);
        }

        return true;
    }

    private static List<AxNode> Innermost(IEnumerable<AxNode> nodes)
    {
        var found = nodes.DistinctBy(node => node.Identity).ToDictionary(node => node.Identity);
        return [.. found.Values.Where(node => !MacRead.Walk(node).Any(inner => inner.Identity != node.Identity && found.ContainsKey(inner.Identity)))];
    }

    private static bool Dialog(AxNode node) =>
        node.Role is "AXDialog" or "AXPopover" or "AXSheet"
        || node.Subrole is "AXApplicationDialog" or "AXApplicationAlertDialog" or "AXDialog" or "AXSystemDialog";

    // Chromium elides the active title, then appends an exact plural tab count. Custom window names have no count and
    // are deliberately refused because the scripting dictionary cannot prove a custom name's identity.
    internal static bool NamesWindow(string name, ChromeWindow window)
    {
        var active = window.SelectedTab;
        if (active is null)
        {
            return false;
        }

        var count = window.Tabs.Count - 1;
        var suffix = count == 0 ? string.Empty : $" and {count.ToString(CultureInfo.InvariantCulture)} other tab{(count == 1 ? string.Empty : "s")}";
        if (suffix.Length > 0 && name.EndsWith(suffix.Replace("other tab", "Other Tab", StringComparison.Ordinal), StringComparison.Ordinal))
        {
            suffix = suffix.Replace("other tab", "Other Tab", StringComparison.Ordinal);
        }

        if (!name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var label = suffix.Length > 0 ? name[..^suffix.Length] : name;
        var title = active.Title.Replace("\n", string.Empty, StringComparison.Ordinal);
        return label == title || (label.EndsWith('…', StringComparison.Ordinal) && label.Length > 4 && title.StartsWith(label[..^1], StringComparison.Ordinal));
    }

    [GeneratedRegex(@" – \d+ tabs?\b")]
    private static partial Regex GroupCountPhrase();

    private sealed record Located(ChromeWindow Window, ChromeTab Tab);

    private sealed record PlacedGroup(long Handle, TabGroup Group);

    private sealed record Live(
        IReadOnlyList<ChromeWindow> Windows,
        ChromeWindow Window,
        ChromeTab Tab,
        AxNode Node,
        AxNode WindowNode,
        int Pid,
        JxaWindow Raw,
        IReadOnlyList<TabSlot> Slots);

    private sealed record TabContent(RuntimeId RuntimeId, string Title, string? Url)
    {
        public static TabContent Of(ChromeTab tab) => new(tab.RuntimeId, tab.Title, tab.Url?.OriginalString);
    }

    private sealed record Strip(JxaWindow Raw, IReadOnlyList<TabSlot> Slots)
    {
        public bool SameAs(IReadOnlyList<TabSlot> slots) =>
            slots.Count == Slots.Count
            && slots.Zip(Slots).All(pair =>
                pair.First.Node.Name == pair.Second.Node.Name
                && pair.First.Node.Selected == pair.Second.Node.Selected
                && pair.First.Group == pair.Second.Group);
    }

    private sealed class NativeAction(ChromeMac driver, MacUi ui, Live live)
    {
        private readonly List<Strip> _watched = [];

        public MacUi Ui { get; } = ui;

        public Live Live { get; } = live;

        public bool Opened { get; private set; }

        // Also hold another window's strip still until the menu choice is pressed.
        public void Watch(long handle)
        {
            var snapshot = driver._jxa.Snapshot();
            var raw = Unique([.. snapshot.Windows.Where(candidate => candidate.Id == handle)], "Watched window missing or ambiguous");
            var found = MacRead.WindowNode(Ui.BrowserWindows(snapshot.Pid), raw, snapshot.Windows);
            var slots = MacRead.AlignedSlots(found, raw);
            if (slots is null)
            {
                throw new ChromeMacException("Watched window's native strip does not match scripting");
            }

            _watched.Add(new Strip(raw, slots));
        }

        // Open a context menu through accessibility, not the mouse: Chrome's tab strip sits in an overlay that
        // hit-testing does not see into, so AXShowMenu needs no click point.
        public void Open(AxNode node)
        {
            if (Menus(Ui.ApplicationTree(Live.Pid)).Count > 0)
            {
                throw new ChromeMacException("A native menu is already open");
            }

            driver.ForegroundOrThrow(Live.Window.Handle);
            Opened = true;
            Ui.ShowMenu(node);
        }

        // Chrome does not answer scripting while one of its menus is open, so this compares the native strips with
        // those verified before the menu opened: the same tabs, in order, with the same titles, selection and groups.
        public void Unchanged()
        {
            var windows = Ui.BrowserWindows(Live.Pid);
            foreach (var strip in new[] { new Strip(Live.Raw, Live.Slots) }.Concat(_watched))
            {
                var found = MacRead.WindowNode(windows, strip.Raw, [strip.Raw]);
                if (found is null || !strip.SameAs(MacRead.TabSlots(found, strip.Raw.Id)))
                {
                    throw new ChromeMacException("Tab strip changed during the action");
                }
            }
        }

        public void Press(AxNode node)
        {
            // An open menu holds Chrome in its modal loop: focus cannot move without closing it, and scripting cannot
            // answer until it closes.
            if (Menus(Ui.ApplicationTree(Live.Pid)).Count == 0)
            {
                driver.ForegroundOrThrow(Live.Window.Handle);
            }
            else if (!Ui.Frontmost(Live.Pid))
            {
                throw new ChromeMacException("Chrome lost foreground focus");
            }

            Ui.Press(node);
        }
    }
}
