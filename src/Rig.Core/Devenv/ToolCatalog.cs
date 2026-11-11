using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Devenv;

/// <summary>How a tool is installed and recognised.</summary>
/// <param name="Winget">The winget package id, on Windows.</param>
/// <param name="Brew">The Homebrew formula or cask, on macOS and Linux.</param>
/// <param name="CheckCmd">A program on the PATH that means the tool is installed, whoever installed it.</param>
public sealed record ToolInstall(string? Winget, string? Brew, string? CheckCmd);

/// <summary>One developer tool <c>rig devenv tools</c> checks for.</summary>
/// <param name="Id">The id; the first catalog to name it wins.</param>
/// <param name="Name">The display name.</param>
/// <param name="Description">What it is.</param>
/// <param name="Category">Its group in the table.</param>
/// <param name="Install">How it is installed.</param>
/// <param name="Required">Whether express and agent setup install it, and whether doctor counts it missing as an issue.</param>
public sealed record DevTool(string Id, string Name, string Description, string Category, ToolInstall Install, bool Required);

/// <summary>
/// The developer tool catalog: the bundled one, then any <c>*.json</c> under <c>~/.config/devenv/tools</c>, then under
/// the working folder's <c>.devenv/tools</c>, then the <c>custom_catalogs</c> listed in <c>~/.config/devenv/config.json</c>.
/// Ported from devenv's <c>config/loader.py</c>.
/// </summary>
public static class ToolCatalog
{
    /// <summary>The bundled catalog's text.</summary>
    public static string BundledText { get; } = ReadBundled();

    /// <summary>
    /// Every tool, the first definition of each id winning. A misshapen catalog throws <see cref="InvalidDataException"/>
    /// rather than quietly contributing nothing; an unreadable <c>config.json</c> lists no custom catalogs.
    /// </summary>
    public static IReadOnlyList<DevTool> Load(string home, string workingDirectory)
    {
        var texts = new List<(string Source, string Text)> { ("the bundled catalog", BundledText) };
        foreach (var folder in new[] { Path.Join(home, ".config", "devenv", "tools"), Path.Join(workingDirectory, ".devenv", "tools") })
        {
            if (Directory.Exists(folder))
            {
                texts.AddRange(Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal).Select(f => (f, File.ReadAllText(f))));
            }
        }

        foreach (var custom in CustomCatalogs(home))
        {
            var path = custom.StartsWith('~') ? Path.Join(home, custom[1..]) : custom;
            if (File.Exists(path))
            {
                texts.Add((path, File.ReadAllText(path)));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. texts.SelectMany(t => Parse(t.Text, t.Source)).Where(tool => seen.Add(tool.Id))];
    }

    /// <summary>A catalog's tools: a bare list, or an object with a <c>tools</c> list.</summary>
    public static IReadOnlyList<DevTool> Parse(string text, string source)
    {
        try
        {
            var root = JsonNode.Parse(text);
            var list = root as JsonArray ?? (root as JsonObject)?["tools"] as JsonArray ?? throw Misshapen(source, "a list, or an object with a tools list");
            return [.. list.Select(entry => Tool(entry as JsonObject ?? throw Misshapen(source, "an object per tool"), source))];
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{source} is not JSON: {e.Message}", e);
        }
        catch (InvalidOperationException e)
        {
            throw Misshapen(source, e.Message);
        }
    }

    private static DevTool Tool(JsonObject entry, string source)
    {
        string Text(JsonNode? node, string field) => node?[field]?.GetValue<string>() ?? throw Misshapen(source, $"a {field} on every tool");
        var install = entry["install"] switch
        {
            null => [],
            JsonObject spec => spec,
            _ => throw Misshapen(source, "an install object"),
        };
        return new DevTool(
            Text(entry, "id"),
            Text(entry, "name"),
            Text(entry, "description"),
            entry["category"]?.GetValue<string>() ?? "utilities",
            new ToolInstall(install["winget"]?.GetValue<string>(), install["brew"]?.GetValue<string>(), install["check_cmd"]?.GetValue<string>()),
            entry["required"]?.GetValue<bool>() ?? false);
    }

    private static List<string> CustomCatalogs(string home)
    {
        try
        {
            var config = JsonNode.Parse(File.ReadAllText(Path.Join(home, ".config", "devenv", "config.json")));
            return config?["custom_catalogs"] is JsonArray paths ? [.. paths.Select(p => p?.GetValue<string>()).OfType<string>()] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static InvalidDataException Misshapen(string source, string wanted) => new($"{source} is not a tool catalog: expected {wanted}.");

    private static string ReadBundled()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Rig.Core.Devenv.dev-tools.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
