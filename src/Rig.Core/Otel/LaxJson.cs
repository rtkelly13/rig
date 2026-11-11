using System.Globalization;
using System.Text.Json;

namespace Rig.Core.Otel;

/// <summary>Reads JSON values the way the Python's pydantic models did in lax mode, so the same files mean the same.</summary>
internal static class LaxJson
{
    private static readonly HashSet<string> Truthy = new(["1", "on", "t", "true", "y", "yes"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Falsy = new(["0", "off", "f", "false", "n", "no"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The last value under <paramref name="key"/>, as Python's json keeps the last of a repeated key; null when absent.</summary>
    public static JsonElement? Field(JsonElement element, string key)
    {
        JsonElement? found = null;
        foreach (var property in element.EnumerateObject())
        {
            found = property.NameEquals(key) ? property.Value : found;
        }

        return found;
    }

    /// <summary>An integer: a whole number, a bool, or a string holding one; null for anything else.</summary>
    public static long? Int(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var whole) => whole,
        { ValueKind: JsonValueKind.Number } n when n.TryGetDouble(out var real) && real == Math.Floor(real) && Math.Abs(real) < 9e18 => (long)real,
        { ValueKind: JsonValueKind.True } => 1,
        { ValueKind: JsonValueKind.False } => 0,
        { ValueKind: JsonValueKind.String } s when long.TryParse(s.GetString()!.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>A bool: true or false, 0 or 1, or one of pydantic's yes/no words; null for anything else.</summary>
    public static bool? Bool(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Number } n when n.TryGetDouble(out var real) && real is 0 or 1 => real == 1,
        { ValueKind: JsonValueKind.String } s when Truthy.Contains(s.GetString()!) => true,
        { ValueKind: JsonValueKind.String } s when Falsy.Contains(s.GetString()!) => false,
        _ => null,
    };
}
