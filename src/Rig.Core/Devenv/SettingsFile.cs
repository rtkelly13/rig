using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;

namespace Rig.Core.Devenv;

/// <summary>A JSON settings file devenv merges into: read whole or not at all, and written without ever truncating it.</summary>
public static class SettingsFile
{
    /// <summary>
    /// The settings at <paramref name="path"/>: empty when there is no file, null when it is not a JSON object (or
    /// repeats a key), since writing it back would lose content.
    /// </summary>
    public static JsonObject? Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(File.ReadAllBytes(path), documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="settings"/> to <paramref name="path"/> as Python's <c>json.dump(indent=2,
    /// ensure_ascii=False)</c> does, CRLF on Windows, through a staging file so a failed write never truncates it. A
    /// symlinked file (a dotfiles checkout) is written through: the link stays and its target changes. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it cannot.
    /// </summary>
    public static void Save(string path, JsonObject settings, bool windows)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var full = Path.GetFullPath(path);
        if (new FileInfo(full).LinkTarget is not null)
        {
            full = File.ResolveLinkTarget(full, returnFinalTarget: true)!.FullName;
        }

        var staging = $"{full}.{Environment.ProcessId}.tmp";
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var text = PyJson.Dumps(settings, ensureAscii: false) + "\n";
            File.WriteAllText(staging, windows ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text);
            File.Move(staging, full, overwrite: true);
        }
        catch
        {
            File.Delete(staging);
            throw;
        }
    }
}
