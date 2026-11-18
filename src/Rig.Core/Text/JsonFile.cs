using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Text;

/// <summary>JSON files read into a <see cref="JsonNode"/>.</summary>
public static class JsonFile
{
    /// <summary>The JSON in <paramref name="path"/>; malformed JSON is an <see cref="InvalidDataException"/> naming the file. A missing or unreadable file still throws its own exception.</summary>
    public static JsonNode? Parse(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    /// <summary>The JSON in <paramref name="path"/>, or null when the file is missing, unreadable or malformed.</summary>
    public static JsonNode? TryParse(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
