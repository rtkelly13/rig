using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// The JXA that reads and acts on Chrome through its macOS scripting dictionary. Each script is sent to osascript byte for byte. Ids are checked before they are
/// spliced in, and page text only ever enters a script as JSON string data.
/// </summary>
public static class JxaScripts
{
    /// <summary>
    /// Chrome's pid, whether it is frontmost, and every window with its tabs. Each property is fetched for every window
    /// (or every tab) in one Apple event: asking tab by tab costs three round trips a tab, seconds on a full browser. A
    /// window or tab that comes or goes between the fetches leaves the lists unaligned, and a window brought forward
    /// reorders them, which only the ids read again afterwards show; either way that read is taken again.
    /// </summary>
    public const string Snapshot = "\n" + """
        ObjC.import('AppKit');
        function run() {
            const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
            if (Number(apps.count) === 0) return JSON.stringify({pid: 0, frontmost: false, windows: []});
            const app = apps.objectAtIndex(0);
            const w = Application('com.google.Chrome').windows;
            for (let attempt = 0; attempt < 3; attempt++) {
                const ids = w.id(), index = w.index(), title = w.title(), active = w.activeTabIndex(),
                    minimized = w.minimized(), mode = w.mode(), bounds = w.bounds(),
                    tabIds = w.tabs.id(), tabTitles = w.tabs.title(), tabUrls = w.tabs.url();
                const order = JSON.stringify([ids, tabIds]) === JSON.stringify([w.id(), w.tabs.id()]);
                const aligned = [index, title, active, minimized, mode, bounds, tabIds, tabTitles, tabUrls]
                    .every(list => list.length === ids.length)
                    && tabIds.every((tabs, i) => tabTitles[i].length === tabs.length && tabUrls[i].length === tabs.length);
                if (!aligned || !order) continue;
                return JSON.stringify({pid: Number(app.processIdentifier), frontmost: Number(app.active) === 1,
                    windows: ids.map((id, i) => ({id: String(id), index: index[i], title: title[i], active: active[i],
                        minimized: minimized[i], mode: mode[i], bounds: bounds[i],
                        tabs: tabIds[i].map((tab, j) => ({id: String(tab), title: tabTitles[i][j], url: tabUrls[i][j]}))}))});
            }
            throw new Error('Chrome windows kept changing during the read');
        }
        """ + "\n";

    /// <summary>The frontmost application's pid, from AppKit; 0 when there is none.</summary>
    public const string Frontmost = "\n" + """
        ObjC.import('AppKit');
        function run() {
            const app = $.NSWorkspace.sharedWorkspace.frontmostApplication;
            return JSON.stringify({pid: app ? Number(app.processIdentifier) : 0});
        }
        """ + "\n";

    /// <summary>The action body that selects the target tab; replies <c>selected</c> or <c>failed</c>.</summary>
    public const string SelectAction = "\n" + """
            window.activeTabIndex = matches[0] + 1;
            return JSON.stringify({status: Number(window.activeTab().id()) === tabID ? 'selected' : 'failed'});
        """ + "\n";

    private const string TabActionTemplate = "\n" + """
        ObjC.import('AppKit');
        function run() {
            const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
            if (Number(apps.count) !== 1) return JSON.stringify({status: 'missing'});
            const chrome = Application('com.google.Chrome');
            const windowID = __WINDOW_ID__, tabID = __TAB_ID__;
            if (chrome.windows.id().filter(id => Number(id) === windowID).length !== 1)
                return JSON.stringify({status: 'missing'});
            const window = chrome.windows.byId(windowID);
            const matches = [];
            window.tabs.id().forEach((id, i) => { if (Number(id) === tabID) matches.push(i); });
            if (matches.length !== 1) return JSON.stringify({status: 'missing'});
            const target = window.tabs.byId(tabID);
            __ACTION__
        }
        """ + "\n";

    private const string CloseTemplate = "\n" + """
        ObjC.import('AppKit');
        function run() {
            const targets = __TARGETS__;
            const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
            if (Number(apps.count) !== 1) return JSON.stringify({statuses: targets.map(() => 'missing')});
            const chrome = Application('com.google.Chrome');
            const close = expected => {
                try {
                    if (chrome.windows.id().filter(id => Number(id) === expected.window).length !== 1) return 'missing';
                    const window = chrome.windows.byId(expected.window);
                    if (window.tabs.id().filter(id => Number(id) === expected.tab).length !== 1) return 'missing';
                    const target = window.tabs.byId(expected.tab);
                    if (target.title() !== expected.title || target.url() !== expected.url) return 'stale';
                    target.close();
                    return 'invoked';
                } catch (error) {
                    return 'failed';
                }
            };
            return JSON.stringify({statuses: targets.map(close)});
        }
        """ + "\n";

    private const string ActivateTemplate = "\n" + """
        ObjC.import('AppKit');
        function run() {
            const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
            if (Number(apps.count) !== 1) return JSON.stringify({status: 'missing'});
            const chrome = Application('com.google.Chrome'), id = __ID__;
            if (chrome.windows.id().filter(windowID => Number(windowID) === id).length !== 1)
                return JSON.stringify({status: 'missing'});
            const window = chrome.windows.byId(id);
            window.minimized = false;
            window.index = 1;
            chrome.activate();
            return JSON.stringify({status: 'invoked'});
        }
        """ + "\n";

    private const string RestoreTemplate = "\n" + """
        ObjC.import('AppKit');
        function run() {
            const state = __STATE__;
            const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
            if (Number(apps.count) === 1) {
                const chrome = Application('com.google.Chrome'), ids = chrome.windows.id().map(Number);
                if (ids.filter(id => id === state.front).length === 1)
                    chrome.windows.byId(state.front).index = 1;
                if (ids.filter(id => id === state.target).length === 1)
                    chrome.windows.byId(state.target).minimized = state.minimized;
            }
            return JSON.stringify({status: 'restored'});
        }
        """ + "\n";

    /// <summary>
    /// The script that finds <paramref name="tab"/> by its window and tab ids, replies <c>missing</c> unless exactly one
    /// matches, and then runs <paramref name="action"/> on it as <c>target</c>.
    /// </summary>
    public static string TabAction(ChromeTab tab, string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var ids = ActionIds.Of(tab);

        // The numeric placeholders go in before the action: page content can hold any placeholder token and must stay
        // plain JSON string data.
        var script = TabActionTemplate
            .Replace("__WINDOW_ID__", ids.Window.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__TAB_ID__", ids.Tab.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return script.Replace("__ACTION__", action, StringComparison.Ordinal);
    }

    /// <summary>
    /// The script that closes each of <paramref name="tabs"/> in turn, found again by its window and tab ids, only while it
    /// still shows the title and URL the snapshot read. It replies one status per tab, in order: <c>invoked</c>,
    /// <c>missing</c>, <c>stale</c> or <c>failed</c>; one tab's failure never stops the rest. The tabs travel as one
    /// ASCII-only JSON array, so page text is only ever string data.
    /// </summary>
    public static string Close(IReadOnlyList<ChromeTab> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var targets = new JsonArray([.. tabs.Select(Target)]);
        return CloseTemplate.Replace("__TARGETS__", JsonOutput.Line(targets), StringComparison.Ordinal);
    }

    private static JsonNode Target(ChromeTab tab)
    {
        var ids = ActionIds.Of(tab);
        var address = tab.Url?.OriginalString ?? throw new ChromeMacException("URL unavailable");
        return new JsonObject { ["window"] = ids.Window, ["tab"] = ids.Tab, ["title"] = tab.Title, ["url"] = address };
    }

    /// <summary>The script that unminimises window <paramref name="handle"/>, brings it to the front and activates Chrome.</summary>
    public static string Activate(long handle)
    {
        if (!JxaDecode.InRange(handle))
        {
            throw new ChromeMacException("Invalid Chrome window identity");
        }

        return ActivateTemplate.Replace("__ID__", handle.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// The script that puts window <paramref name="front"/> back in front and window <paramref name="target"/>'s
    /// minimised state back; both ids come from a decoded snapshot.
    /// </summary>
    public static string Restore(long target, long front, bool minimized)
    {
        var state = JsonOutput.Line(new JsonObject { ["target"] = target, ["front"] = front, ["minimized"] = minimized });
        return RestoreTemplate.Replace("__STATE__", state, StringComparison.Ordinal);
    }
}
