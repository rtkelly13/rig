using System.Text.Json.Nodes;
using Rig.Core.Chrome.Extension;

namespace Rig.Core.Tests.Chrome.Extension;

/// <summary>
/// One profile's extension, kept as the JSON its <c>windows</c> command returns and changed the way Chrome's APIs
/// change it, so the driver is tested against behaviour rather than call order.
/// </summary>
internal sealed class FakeExtension
{
    private readonly List<Win> _windows = [];
    private readonly List<(int Id, long Window, string Title)> _groups = [];
    private int _nextGroup = 100;

    public FakeExtension(string email = "me@example.com", string browser = "/usr/lib/chromium/chromium", string socket = "/run/rig/chrome-tabs/1.sock") =>
        Connection = new ExtensionConnection(socket, email, "0.1.0", browser);

    public ExtensionConnection Connection { get; }

    public List<string> Commands { get; } = [];

    /// <summary>A command answers with this error instead of acting.</summary>
    public string? Refuse { get; set; }

    public FakeExtension Window(long id, params (int Id, string Title, string Url)[] tabs)
    {
        _windows.Add(new Win(id, [.. tabs.Select(tab => new Tab(tab.Id, tab.Title, tab.Url))]));
        return this;
    }

    public FakeExtension Group(long window, string title, params int[] tabIds)
    {
        var id = _nextGroup++;
        _groups.Add((id, window, title));
        foreach (var tab in All().Where(tab => tabIds.Contains(tab.Id)))
        {
            tab.Group = id;
        }

        return this;
    }

    public void Retitle(int tabId, string title) => All().Single(tab => tab.Id == tabId).Title = title;

    public JsonNode? Send(string command, JsonObject? args)
    {
        Commands.Add(command);
        if (command != ExtensionProtocol.WindowsCommand && Refuse is { } error)
        {
            throw new ExtensionCallException(error);
        }

        switch (command)
        {
            case ExtensionProtocol.WindowsCommand:
                return Snapshot();
            case ExtensionProtocol.CloseCommand:
                var closed = TabOf(args!);
                _windows.ForEach(window => window.Tabs.Remove(closed));
                return null;
            case ExtensionProtocol.ActivateCommand:
                var active = TabOf(args!);
                foreach (var tab in WindowOf(active).Tabs)
                {
                    tab.Active = tab == active;
                }

                return null;
            case ExtensionProtocol.GroupCommand:
                var ids = args!["tabIds"]!.AsArray().Select(id => (int)id!).ToList();
                var group = args["groupId"] is { } existing ? (int)existing : _nextGroup++;
                if (args["groupId"] is null)
                {
                    _groups.Add((group, (long)args["windowId"]!, (string)args["title"]!));
                }

                var home = _groups.Single(known => known.Id == group).Window;
                foreach (var id in ids)
                {
                    var tab = All().Single(tab => tab.Id == id);
                    WindowOf(tab).Tabs.Remove(tab);
                    _windows.Single(window => window.Id == home).Tabs.Add(tab);
                    tab.Group = group;
                }

                return new JsonObject { ["groupId"] = group };
            case ExtensionProtocol.UngroupCommand:
                foreach (var id in args!["tabIds"]!.AsArray().Select(id => (int)id!))
                {
                    All().Single(tab => tab.Id == id).Group = -1;
                }

                return null;
            case ExtensionProtocol.MoveCommand:
                var moved = TabOf(args!);
                WindowOf(moved).Tabs.Remove(moved);
                _windows.Single(window => window.Id == (long)args!["windowId"]!).Tabs.Add(moved);
                return null;
            default:
                throw new ExtensionCallException($"unknown command {command}");
        }
    }

    private JsonObject Snapshot() => new()
    {
        ["focusedWindowId"] = _windows.Count > 0 ? _windows[0].Id : null,
        ["windows"] = new JsonArray([.. _windows.Select(window => (JsonNode)new JsonObject
        {
            ["id"] = window.Id,
            ["state"] = "normal",
            ["incognito"] = false,
            ["left"] = 0,
            ["top"] = 0,
            ["width"] = 800,
            ["height"] = 600,
            ["tabs"] = new JsonArray([.. window.Tabs.Select((tab, index) => (JsonNode)new JsonObject
            {
                ["id"] = tab.Id,
                ["windowId"] = window.Id,
                ["index"] = index,
                ["title"] = tab.Title,
                ["url"] = tab.Url,
                ["active"] = tab.Active,
                ["pinned"] = false,
                ["discarded"] = false,
                ["audible"] = false,
                ["groupId"] = tab.Group,
            })]),
        })]),
        ["groups"] = new JsonArray([.. _groups
            .Where(group => All().Any(tab => tab.Group == group.Id))
            .Select(group => (JsonNode)new JsonObject { ["id"] = group.Id, ["windowId"] = group.Window, ["title"] = group.Title, ["color"] = "grey", ["collapsed"] = false })]),
    };

    private IEnumerable<Tab> All() => _windows.SelectMany(window => window.Tabs);

    private Tab TabOf(JsonObject args) => All().FirstOrDefault(tab => tab.Id == (int)args["tabId"]!) ?? throw new ExtensionCallException($"No tab with id: {args["tabId"]}");

    private Win WindowOf(Tab tab) => _windows.Single(window => window.Tabs.Contains(tab));

    private sealed record Win(long Id, List<Tab> Tabs);

    private sealed class Tab(int id, string title, string url)
    {
        public int Id { get; } = id;

        public string Title { get; set; } = title;

        public string Url { get; } = url;

        public bool Active { get; set; }

        public int Group { get; set; } = -1;
    }
}

/// <summary>A bridge over fake extensions, one per profile.</summary>
internal sealed class FakeBridge(params FakeExtension[] extensions) : IExtensionBridge
{
    public IReadOnlyList<ExtensionConnection> Connections() => [.. extensions.Select(extension => extension.Connection)];

    public JsonNode? Send(ExtensionConnection connection, string command, JsonObject? args = null) =>
        extensions.Single(extension => extension.Connection == connection).Send(command, args);
}
