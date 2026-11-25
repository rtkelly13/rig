using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Chrome.MacOS;

namespace Rig.Core.Chrome.Extension;

/// <summary>
/// The Chrome driver that works through rig's extension (<c>extensions/chrome-tabs</c>) instead of the screen: Chrome's
/// own tab and group APIs read every profile's windows and carry out each step, so nothing is brought forward and the
/// mouse is never used. Each step reads again first and refuses a tab that moved, changed or went away. Saved groups
/// are out of the extension APIs' reach, so there are none to read or delete.
/// </summary>
/// <param name="bridge">Reaches the extension in each running profile.</param>
/// <param name="profiles">
/// A browser's profiles by directory, given its config folder (<see cref="ExtensionConnection.ConfigFolder"/>), to name
/// the profile each extension runs in.
/// </param>
/// <param name="time">Times each step.</param>
public sealed class ChromeExtension(IExtensionBridge bridge, Func<string, IReadOnlyDictionary<string, ProfileInfo>> profiles, TimeProvider time)
    : IChromeWindowReader, IChromeTabActions, IChromeGroupActions, IChromeSavedGroupActions
{
    /// <summary>The message when no profile has the extension connected.</summary>
    public const string NotConnected =
        "No Chrome profile has the rig extension connected. Run `rig chrome-tabs extension install`, restart Chromium, then check `rig chrome-tabs extension status`.";

    /// <inheritdoc/>
    public IReadOnlyList<ChromeWindow> ListWindows() => [.. Read().SelectMany(read => read.Windows).OrderBy(window => window.AgeRank)];

    /// <inheritdoc/>
    public long ForegroundWindow() =>
        Read().Where(read => read.Snapshot.FocusedWindowId is not null).Select(read => ExtensionSnapshot.Handle(read.Space, read.Snapshot.FocusedWindowId!.Value)).FirstOrDefault();

    /// <inheritdoc/>
    public IReadOnlyList<CloseOutcome> CloseTabs(IReadOnlyList<ChromeTab> tabs) => [.. tabs.Select(CloseTab)];

    /// <summary>Closes <paramref name="tab"/>, found again by its runtime id.</summary>
    public CloseOutcome CloseTab(ChromeTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var started = time.GetTimestamp();
        CloseOutcome Outcome(bool closed, string detail) => new(closed, detail, time.GetElapsedTime(started));
        try
        {
            var live = Live(tab);
            if (live.Tab.Pinned)
            {
                return Outcome(false, "pinned; refusing close");
            }

            _ = bridge.Send(live.Connection, ExtensionProtocol.CloseCommand, new JsonObject { ["tabId"] = TabId(tab) });
            return Find(Read(), tab) is null ? Outcome(true, "closed") : Outcome(false, "tab still present after close");
        }
        catch (Exception error) when (error is ExtensionCallException or ChromeReadException or ExtensionStepRefusedException)
        {
            return Outcome(false, error.Message);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<long, ChromeTab?> RestoreSelection(IEnumerable<ChromeWindow> before, IReadOnlyDictionary<long, ChromeTab>? fallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        var restored = new Dictionary<long, ChromeTab?>();
        var reads = Read();
        foreach (var window in before)
        {
            ChromeTab? chosen = null;
            foreach (var candidate in new[] { window.SelectedTab, fallbacks?.GetValueOrDefault(window.Handle) })
            {
                if (candidate is null || candidate.WindowHandle != window.Handle || Find(reads, candidate) is not { } live || live.Window.Handle != window.Handle)
                {
                    continue;
                }

                try
                {
                    _ = bridge.Send(live.Connection, ExtensionProtocol.ActivateCommand, new JsonObject { ["tabId"] = TabId(candidate) });
                    chosen = live.Tab with { Selected = true };
                    break;
                }
                catch (ExtensionCallException)
                {
                    // Try the fallback.
                }
            }

            restored[window.Handle] = chosen;
        }

        return restored;
    }

    /// <summary>The extension acts on a window wherever it is, so nothing is brought forward and nothing needs putting back.</summary>
    public ForegroundLease Foreground(long handle, TimeSpan? timeout = null) => new(arrived: true);

    /// <inheritdoc/>
    public GroupOutcome GroupTab(ChromeTab tab, string title, bool isNew, long? homeHandle = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(title);
        var started = time.GetTimestamp();
        GroupOutcome Outcome(bool grouped, string detail) => new(grouped, detail, time.GetElapsedTime(started));
        try
        {
            if (title.Length == 0)
            {
                throw new ExtensionStepRefusedException("Group title is empty");
            }

            if (tab.Group is not null)
            {
                throw new ExtensionStepRefusedException("Tab is already grouped");
            }

            var reads = Read();
            var live = Live(tab, reads);
            if (live.Tab.Group is not null)
            {
                throw new ExtensionStepRefusedException("Tab is already grouped");
            }

            var home = homeHandle ?? tab.WindowHandle;
            var titled = Groups(live.Read, title);
            JsonObject args;
            if (isNew)
            {
                if (home != tab.WindowHandle || titled.Count > 0)
                {
                    throw new ExtensionStepRefusedException("New group needs its home window and a unique title");
                }

                args = new JsonObject { ["tabIds"] = new JsonArray(TabId(tab)), ["windowId"] = ExtensionSnapshot.WindowId(home), ["title"] = title };
            }
            else
            {
                if (titled.Count != 1)
                {
                    throw new ExtensionStepRefusedException("Target group missing or ambiguous");
                }

                if (ExtensionSnapshot.Handle(live.Read.Space, titled[0].WindowId) != home)
                {
                    throw new ExtensionStepRefusedException("Target group is in a different window");
                }

                args = new JsonObject { ["tabIds"] = new JsonArray(TabId(tab)), ["groupId"] = titled[0].Id };
            }

            _ = bridge.Send(live.Connection, ExtensionProtocol.GroupCommand, args);
            return Find(Read(), tab) is { } after && after.Tab.Group?.Title == title && after.Window.Handle == home
                ? Outcome(true, isNew ? "created" : "joined")
                : Outcome(false, "tab is not in the group after grouping");
        }
        catch (Exception error) when (error is ExtensionCallException or ChromeReadException or ExtensionStepRefusedException)
        {
            return Outcome(false, error.Message);
        }
    }

    /// <inheritdoc/>
    public GroupOutcome Ungroup(ChromeTab tab, string title, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(title);
        var started = time.GetTimestamp();
        GroupOutcome Outcome(bool done, string detail) => new(done, detail, time.GetElapsedTime(started));
        try
        {
            var live = Live(tab);
            if (tab.Group is null || live.Tab.Group != tab.Group || tab.Group.Title != title)
            {
                throw new ExtensionStepRefusedException("Tab is no longer in the requested group");
            }

            var members = new JsonArray([.. live.Window.Tabs.Where(member => member.Group == tab.Group).Select(member => (JsonNode)TabId(member))]);
            _ = bridge.Send(live.Connection, ExtensionProtocol.UngroupCommand, new JsonObject { ["tabIds"] = members });
            return Find(Read(), tab) is { } after && after.Tab.Group is null
                ? Outcome(true, "ungrouped")
                : Outcome(false, "tab is still grouped");
        }
        catch (Exception error) when (error is ExtensionCallException or ChromeReadException or ExtensionStepRefusedException)
        {
            return Outcome(false, error.Message);
        }
    }

    /// <inheritdoc/>
    public MoveOutcome MoveToWindow(ChromeTab tab, long targetHandle, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var started = time.GetTimestamp();
        MoveOutcome Outcome(bool moved, string detail, ChromeTab? arrived = null) => new(new GroupOutcome(moved, detail, time.GetElapsedTime(started)), arrived);
        try
        {
            if (tab.Group is not null || targetHandle == tab.WindowHandle)
            {
                throw new ExtensionStepRefusedException("Move requires an ungrouped tab and a different window");
            }

            var reads = Read();
            var live = Live(tab, reads);
            if (!live.Read.Windows.Any(window => window.Handle == targetHandle))
            {
                throw new ExtensionStepRefusedException("Target window is gone or in another profile");
            }

            _ = bridge.Send(live.Connection, ExtensionProtocol.MoveCommand, new JsonObject { ["tabId"] = TabId(tab), ["windowId"] = ExtensionSnapshot.WindowId(targetHandle) });
            return Find(Read(), tab) is { } after && after.Window.Handle == targetHandle
                ? Outcome(true, "moved", after.Tab)
                : Outcome(false, "tab is not in the target window after the move");
        }
        catch (Exception error) when (error is ExtensionCallException or ChromeReadException or ExtensionStepRefusedException)
        {
            return Outcome(false, error.Message);
        }
    }

    /// <summary>The extension APIs cannot see saved groups, so there are none.</summary>
    public IReadOnlyList<SavedGroup> SavedGroups(long handle, string profile) => [];

    /// <summary>The extension APIs cannot delete saved groups.</summary>
    public SavedDeleteOutcome DeleteSavedGroup(long handle, string profile, string title, TimeSpan? timeout = null) =>
        new(false, "saved groups are out of the extension's reach", [], TimeSpan.Zero);

    private List<ProfileRead> Read()
    {
        var connections = bridge.Connections();
        if (connections.Count == 0)
        {
            throw new ChromeReadException(NotConnected);
        }

        var reads = new List<ProfileRead>();
        foreach (var (connection, number) in connections.Select((connection, index) => (connection, index)))
        {
            JsonNode? result;
            try
            {
                result = bridge.Send(connection, ExtensionProtocol.WindowsCommand);
            }
            catch (ExtensionCallException error)
            {
                throw new ChromeReadException($"Could not read a profile's windows: {error.Message}", error);
            }

            var snapshot = ExtensionSnapshot.Parse(result);
            var known = connection.ConfigFolder is { } folder ? profiles(folder) : new Dictionary<string, ProfileInfo>();
            var sameBrowser = connections.Count(other => other.ConfigFolder == connection.ConfigFolder);
            var (dir, name, scope) = Profile(connection, sameBrowser, known);
            var space = Space(connection, number);
            reads.Add(new ProfileRead(connection, space, snapshot, ExtensionSnapshot.Windows(snapshot, space, dir, name, scope)));
        }

        return reads;
    }

    private static (string? Dir, string? Name, string? Scope) Profile(ExtensionConnection connection, int connections, IReadOnlyDictionary<string, ProfileInfo> known)
    {
        var byEmail = connection.Email.Length == 0
            ? []
            : known.Where(entry => string.Equals(entry.Value.UserName, connection.Email, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byEmail.Count == 1)
        {
            return (byEmail[0].Key, byEmail[0].Value.Name, Scope(connection, byEmail[0].Key));
        }

        if (connection.ConfigFolder is not null && connections == 1 && known.Count == 1)
        {
            var only = known.First();
            return (only.Key, only.Value.Name, Scope(connection, only.Key));
        }

        return (null, connection.Email.Length > 0 ? connection.Email : null, $"extension:{Path.GetFileNameWithoutExtension(connection.Socket)}");
    }

    /// <summary>
    /// The planning scope of a named profile: its browser's folder and the profile's, so Chromium's and Chrome's
    /// <c>Default</c> profiles are never planned as one.
    /// </summary>
    private static string Scope(ExtensionConnection connection, string dir) => $"{connection.ConfigFolder}/{dir}";

    /// <summary>
    /// The connection's id space: its host's process id, taken from the socket's name, which no other connected host
    /// shares; its position among the connections when the name is not a number.
    /// </summary>
    private static int Space(ExtensionConnection connection, int position) =>
        int.TryParse(Path.GetFileNameWithoutExtension(connection.Socket), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : -1 - position;

    private LiveTab Live(ChromeTab tab, IReadOnlyList<ProfileRead>? reads = null)
    {
        var live = Find(reads ?? Read(), tab) ?? throw new ExtensionStepRefusedException("tab no longer present");
        if (live.Window.Handle != tab.WindowHandle)
        {
            throw new ExtensionStepRefusedException("tab moved to another window");
        }

        if (live.Tab.Title != tab.Title || live.Tab.Url?.OriginalString != tab.Url?.OriginalString)
        {
            throw new ExtensionStepRefusedException("tab changed since it was read");
        }

        return live;
    }

    private static LiveTab? Find(IReadOnlyList<ProfileRead> reads, ChromeTab tab)
    {
        foreach (var read in reads)
        {
            foreach (var window in read.Windows)
            {
                if (window.Tabs.FirstOrDefault(current => current.RuntimeId.Equals(tab.RuntimeId)) is { } found)
                {
                    return new LiveTab(read, window, found);
                }
            }
        }

        return null;
    }

    private static List<SnapshotGroup> Groups(ProfileRead read, string title) =>
        [.. read.Snapshot.Groups.Where(group => group.Title == title && read.Windows.Any(window => window.Handle == ExtensionSnapshot.Handle(read.Space, group.WindowId)))];

    private static int TabId(ChromeTab tab) => tab.RuntimeId.Parts is [_, var id] ? id : throw new ExtensionStepRefusedException("not a tab this driver read");

    private sealed record ProfileRead(ExtensionConnection Connection, int Space, Snapshot Snapshot, IReadOnlyList<ChromeWindow> Windows);

    private sealed record LiveTab(ProfileRead Read, ChromeWindow Window, ChromeTab Tab)
    {
        public ExtensionConnection Connection => Read.Connection;
    }
}
