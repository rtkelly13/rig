using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.ReviewQueue;

/// <summary>The existing snake-case snapshots, in a canonical one-line JSON spelling for change detection.</summary>
public static class QueueJson
{
    /// <summary>Typed boundary settings, retaining defaults for old snapshots while rejecting missing required fields.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>Decode a persisted snapshot; old rows can omit body and facts.</summary>
    public static QueueItem DecodeItem(string payload)
    {
        var item = JsonSerializer.Deserialize<QueueItem>(payload, Options) ?? throw new JsonException("Expected a triage item");
        if (item.Kind is not ("pr" or "issue"))
        {
            throw new JsonException("Invalid triage item kind");
        }

        return item;
    }

    /// <summary>The canonical one-line snapshot: properties sorted, so a rewrite of the same data is byte-identical.</summary>
    public static string EncodeItem(QueueItem item) =>
        JsonOutput.Line(Sorted(JsonSerializer.SerializeToNode(item, Options)));

    /// <summary>
    /// Whether two snapshots hold the same data. Rows written before the writer changed are in an older JSON spelling,
    /// so a re-encode of unchanged data must not look like an observation.
    /// </summary>
    public static bool SameSnapshot(string left, string right)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject([.. obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Sorted(pair.Value)))]),
        JsonArray array => new JsonArray([.. array.Select(Sorted)]),
        _ => node.DeepClone(),
    };
}
