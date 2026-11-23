using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Omarchy;

/// <summary>One leaf of <c>shell.json</c> that differs from the policy.</summary>
/// <param name="Path">The leaf's path under the document, such as <c>bar.position</c>.</param>
/// <param name="Have">What the file holds, or <c>(missing)</c>.</param>
/// <param name="Want">What the policy declares.</param>
internal sealed record ShellDifference(string Path, string Have, string Want);

/// <summary>
/// The deep merge <c>rig omarchy sync</c> applies to Omarchy's user-owned <c>shell.json</c>: objects merge key by key,
/// arrays and scalars replace, and every key the policy leaves out is kept.
/// </summary>
internal static class ShellDocument
{
    /// <summary><paramref name="current"/> with every key in <paramref name="desired"/> merged over it.</summary>
    public static JsonObject Merge(JsonObject current, JsonObject desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        var merged = (JsonObject)current.DeepClone();
        MergeInto(merged, desired);
        return merged;
    }

    /// <summary>Every leaf the policy declares that <paramref name="current"/> does not already hold.</summary>
    public static IReadOnlyList<ShellDifference> Differences(JsonObject current, JsonObject desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        List<ShellDifference> differences = [];
        Diff(current, desired, string.Empty, differences);
        return differences;
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject wanted && target[key] is JsonObject existing)
            {
                MergeInto(existing, wanted);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static void Diff(JsonObject? current, JsonObject desired, string prefix, List<ShellDifference> differences)
    {
        foreach (var (key, want) in desired)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            JsonNode? have = null;
            var exists = current is not null && current.TryGetPropertyValue(key, out have);
            DiffNode(exists, have, want, path, differences);
        }
    }

    private static void DiffNode(bool exists, JsonNode? have, JsonNode? want, string path, List<ShellDifference> differences)
    {
        if (want is JsonObject wantedObject && (wantedObject.Count > 0 || (exists && have is JsonObject)))
        {
            // An object merges key by key, so an empty one declares nothing over an existing object.
            Diff(exists ? have as JsonObject : null, wantedObject, path, differences);
            return;
        }

        if (exists && want is JsonArray wantedArray && have is JsonArray haveArray)
        {
            DiffArray(haveArray, wantedArray, path, differences);
            return;
        }

        if (!exists || !JsonNode.DeepEquals(have, want))
        {
            differences.Add(new(path, exists ? Format(have) : "(missing)", Format(want)));
        }
    }

    private static void DiffArray(JsonArray have, JsonArray want, string path, List<ShellDifference> differences)
    {
        for (var index = 0; index < Math.Max(have.Count, want.Count); index++)
        {
            var itemPath = $"{path}[{index}]";
            if (index >= want.Count)
            {
                differences.Add(new(itemPath, Format(have[index]), "(removed)"));
            }
            else if (index >= have.Count)
            {
                differences.Add(new(itemPath, "(missing)", Format(want[index])));
            }
            else if (!JsonNode.DeepEquals(have[index], want[index]))
            {
                // Merge replaces an array whole, so an element must match exactly: a key the policy omits is drift.
                differences.Add(new(itemPath, Format(have[index]), Format(want[index])));
            }
        }
    }

    private static string Format(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        var text = JsonOutput.Line(node, keepNonAscii: true);
        return text.Length <= 120 ? text : text[..117] + "...";
    }
}
