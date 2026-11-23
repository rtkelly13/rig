using System.Text.Json;

namespace Rig.Paket;

/// <summary>The sidecar's output: one entry per requested file, in request order.</summary>
internal static class PaketJson
{
    internal static string Write(IReadOnlyList<FileReading> readings)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartArray("files");
            foreach (var reading in readings)
            {
                WriteFile(json, reading);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFile(Utf8JsonWriter json, FileReading reading)
    {
        json.WriteStartObject();
        json.WriteString("path", reading.Path);
        json.WriteString("kind", reading.Kind);
        json.WriteString("error", reading.Error);
        json.WriteString("warning", reading.Warning);
        json.WriteStartArray("groups");
        foreach (var group in reading.Groups)
        {
            json.WriteStartObject();
            json.WriteString("name", group.Name);
            json.WriteStartArray("packages");
            foreach (var package in group.Packages)
            {
                json.WriteStartObject();
                json.WriteString("name", package.Name);
                json.WriteString("version", package.Version);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }
}
