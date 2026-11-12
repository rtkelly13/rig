using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Policy;

namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// The JXA that reads and acts on Chrome through its macOS scripting dictionary. Each script is the text Python's
/// <c>desktop_manager.chrome.drivers.macos_jxa</c> sent to osascript, byte for byte. Ids are checked before they are
/// spliced in, and page text only ever enters a script as JSON string data.
/// </summary>
public static class JxaScripts
{
    /// <summary>Chrome's pid, whether it is frontmost, and every window with its tabs.</summary>
    public const string Snapshot = "\n" + """
        ObjC.import('AppKit');
        function run() {
            const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
            if (Number(apps.count) === 0) return JSON.stringify({pid: 0, frontmost: false, windows: []});
            const app = apps.objectAtIndex(0);
            const chrome = Application('com.google.Chrome');
            return JSON.stringify({pid: Number(app.processIdentifier), frontmost: Number(app.active) === 1,
                windows: chrome.windows().map(w => ({id: String(w.id()), index: w.index(),
                    title: w.title(), active: w.activeTabIndex(), minimized: w.minimized(), mode: w.mode(),
                    bounds: w.bounds(), tabs: w.tabs().map(t => ({id: String(t.id()), title: t.title(),
                        url: t.url()}))}))});
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
            const windows = chrome.windows().filter(w => Number(w.id()) === windowID);
            if (windows.length !== 1) return JSON.stringify({status: 'missing'});
            const window = chrome.windows.byId(windowID), tabs = window.tabs();
            const matches = [];
            tabs.forEach((t, i) => { if (Number(t.id()) === tabID) matches.push(i); });
            if (matches.length !== 1) return JSON.stringify({status: 'missing'});
            const target = window.tabs.byId(tabID);
            __ACTION__
        }
        """ + "\n";

    private const string CloseActionBody = "\n" + """
            if (target.title() !== expected.title || target.url() !== expected.url)
                return JSON.stringify({status: 'stale'});
            target.close();
            return JSON.stringify({status: 'invoked'});
        """ + "\n";

    private const string ActivateTemplate = "\n" + """
        ObjC.import('AppKit');
        function run() {
            const apps = $.NSRunningApplication.runningApplicationsWithBundleIdentifier('com.google.Chrome');
            if (Number(apps.count) !== 1) return JSON.stringify({status: 'missing'});
            const chrome = Application('com.google.Chrome'), id = __ID__;
            if (chrome.windows().filter(w => Number(w.id()) === id).length !== 1)
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
                const chrome = Application('com.google.Chrome'), windows = chrome.windows();
                if (windows.filter(w => Number(w.id()) === state.front).length === 1)
                    chrome.windows.byId(state.front).index = 1;
                if (windows.filter(w => Number(w.id()) === state.target).length === 1)
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
    /// The action body that closes the target only while it still shows <paramref name="title"/> and
    /// <paramref name="address"/> (the exact text the snapshot read), passed as one ASCII-only JSON object; replies <c>stale</c> otherwise.
    /// </summary>
    public static string CloseAction(string title, string address)
    {
        var expected = PyJson.DumpsLine(new JsonObject { ["title"] = title, ["url"] = address });
        return $"const expected = {expected};\n{CloseActionBody}";
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
        var state = PyJson.DumpsLine(new JsonObject { ["target"] = target, ["front"] = front, ["minimized"] = minimized });
        return RestoreTemplate.Replace("__STATE__", state, StringComparison.Ordinal);
    }
}
