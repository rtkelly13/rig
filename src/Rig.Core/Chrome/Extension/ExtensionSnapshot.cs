using System.Text.Json.Nodes;

namespace Rig.Core.Chrome.Extension;

/// <summary>One tab group as the extension reports it.</summary>
/// <param name="Id">Chrome's group id.</param>
/// <param name="WindowId">The window holding it.</param>
/// <param name="Title">Its title; "" when untitled.</param>
public sealed record SnapshotGroup(int Id, long WindowId, string Title);

/// <summary>A profile's windows as the extension's <c>windows</c> command reports them.</summary>
/// <param name="FocusedWindowId">The last focused normal window, if any.</param>
/// <param name="Windows">The windows' JSON, as sent.</param>
/// <param name="Groups">The tab groups.</param>
public sealed record Snapshot(long? FocusedWindowId, IReadOnlyList<JsonObject> Windows, IReadOnlyList<SnapshotGroup> Groups);

/// <summary>Reads the extension's <c>windows</c> answer into rig's Chrome model.</summary>
public static class ExtensionSnapshot
{
    /// <summary>Parses the answer; a missing or malformed field is a <see cref="ChromeReadException"/>.</summary>
    public static Snapshot Parse(JsonNode? result)
    {
        if (result is not JsonObject root || root["windows"] is not JsonArray windows)
        {
            throw new ChromeReadException("The extension's windows answer has no windows");
        }

        var groups = root["groups"] is JsonArray list
            ? list.OfType<JsonObject>().Select(group => new SnapshotGroup(Int(group, "id"), Long(group, "windowId"), Text(group, "title"))).ToList()
            : [];
        return new Snapshot(OptionalLong(root, "focusedWindowId"), [.. windows.OfType<JsonObject>()], groups);
    }

    /// <summary>
    /// The snapshot's windows, in the profile <paramref name="profileDir"/> named <paramref name="profileName"/>.
    /// Chrome's window ids rise with creation, so they serve as both handle and age; incognito windows are left out.
    /// </summary>
    public static IReadOnlyList<ChromeWindow> Windows(Snapshot snapshot, string? profileDir, string? profileName, string? scope)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var groups = snapshot.Groups.ToDictionary(group => group.Id);
        var windows = new List<ChromeWindow>();
        foreach (var window in snapshot.Windows)
        {
            if (Bool(window, "incognito"))
            {
                continue;
            }

            var handle = Long(window, "id");
            var tabs = new List<ChromeTab>();
            foreach (var tab in (window["tabs"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var title = Text(tab, "title");
                var groupId = OptionalLong(tab, "groupId") ?? -1;
                tabs.Add(new ChromeTab(
                    new RuntimeId([Int(tab, "id")]),
                    handle,
                    Int(tab, "index"),
                    title,
                    title,
                    Bool(tab, "active"),
                    Bool(tab, "pinned"),
                    !Bool(tab, "discarded"),
                    Bool(tab, "audible"))
                {
                    Group = groupId >= 0 && groups.TryGetValue((int)groupId, out var group) ? new TabGroup(new RuntimeId([group.Id]), group.Title) : null,
                    Url = Uri.TryCreate(Text(tab, "url"), UriKind.Absolute, out var url) ? url : null,
                });
            }

            tabs.Sort((left, right) => left.Index.CompareTo(right.Index));
            var title0 = tabs.FirstOrDefault(tab => tab.Selected)?.Title ?? "";
            windows.Add(new ChromeWindow(handle, title0, profileDir, profileName, handle, Text(window, "state") == "minimized", null, tabs)
            {
                Rect = Rect(window),
                ProfileScope = scope,
            });
        }

        return windows;
    }

    private static WindowRect? Rect(JsonObject window) =>
        OptionalLong(window, "left") is { } left && OptionalLong(window, "top") is { } top
        && OptionalLong(window, "width") is { } width && OptionalLong(window, "height") is { } height
            ? new WindowRect((int)left, (int)top, (int)(left + width), (int)(top + height))
            : null;

    private static string Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static bool Bool(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static long? OptionalLong(JsonObject node, string name) => node[name] switch
    {
        JsonValue value when value.TryGetValue<long>(out var whole) => whole,
        JsonValue value when value.TryGetValue<int>(out var small) => small,
        JsonValue value when value.TryGetValue<double>(out var number) => (long)number,
        _ => null,
    };

    private static long Long(JsonObject node, string name) =>
        OptionalLong(node, name) ?? throw new ChromeReadException($"The extension's answer has no {name}");

    private static int Int(JsonObject node, string name) => checked((int)Long(node, name));
}
