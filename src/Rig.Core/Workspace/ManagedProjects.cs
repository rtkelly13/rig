using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Workspace;

/// <summary>A project declared in <c>config/managed-projects.json</c>, or retired in the archive file beside it.</summary>
/// <param name="Name">The clone's folder name.</param>
/// <param name="Repo">Its remote, such as <c>git@github.com:owner/name.git</c>, or <c>local/…</c> for one with no remote.</param>
/// <param name="Description">What it is.</param>
/// <param name="Domain">Its domain, for the estate router table.</param>
/// <param name="Stack">Its tech stack, for the estate router table.</param>
/// <param name="Branch">Its primary branch, when it is not the default.</param>
/// <param name="Enabled">False for a retired project, whose clone is kept as an archive.</param>
/// <param name="OrcaGroup">The Orca group it is shown in.</param>
public sealed record ManagedRepoSpec(
    string Name,
    string Repo,
    string Description = "",
    string Domain = "",
    string Stack = "",
    string? Branch = null,
    bool Enabled = true,
    string? OrcaGroup = null);

/// <summary>Reads the managed-project registry. Ported from <c>rig.workspace.engine.load_managed_specs</c>.</summary>
public static class ManagedProjects
{
    /// <summary>The registry's file name under a rig checkout's <c>config/</c>.</summary>
    public const string FileName = "managed-projects.json";

    /// <summary>The retired projects' file, beside the registry.</summary>
    public const string ArchivedFileName = "archived-projects.json";

    /// <summary>
    /// The registry rig reads: <paramref name="checkout"/>'s, else the one under <paramref name="workingDirectory"/>,
    /// as Python's <c>rig.roots.checkout_config</c> chose.
    /// </summary>
    public static string RegistryFile(string checkout, string workingDirectory)
    {
        var candidate = Path.Join(checkout, "config", FileName);
        return File.Exists(candidate) ? candidate : Path.Join(workingDirectory, "config", FileName);
    }

    /// <summary>
    /// Every project in <paramref name="path"/>, then every retired one in the archive file beside it, disabled so
    /// their clones stay retained archives and are never offered for cleanup. A missing or malformed file has no
    /// projects, and an entry with a field of the wrong type is skipped.
    /// </summary>
    public static IReadOnlyList<ManagedRepoSpec> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return [];
        }

        return [.. Decode(path, retired: false), .. Decode(Path.Join(Path.GetDirectoryName(path), ArchivedFileName), retired: true)];
    }

    private static List<ManagedRepoSpec> Decode(string path, bool retired)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }

        return root is JsonObject file && file["projects"] is JsonArray projects
            ? projects.Select(entry => Entry(entry, retired)).OfType<ManagedRepoSpec>().ToList()
            : [];
    }

    private static ManagedRepoSpec? Entry(JsonNode? node, bool retired)
    {
        if (node is not JsonObject entry
            || !Text(entry, "name", out var name)
            || !Text(entry, "repo", out var repo)
            || !Text(entry, "description", out var description)
            || !Text(entry, "domain", out var domain)
            || !Text(entry, "stack", out var stack)
            || !OptionalText(entry, "branch", out var branch)
            || !Flag(entry, "enabled", out var enabled)
            || !Orca(entry, out var group))
        {
            return null;
        }

        return new(name, repo, description, domain, stack, branch, enabled && !retired, group);
    }

    // A string field, empty when absent; null or another type is not a string.
    private static bool Text(JsonObject entry, string key, out string value)
    {
        value = string.Empty;
        return !entry.TryGetPropertyValue(key, out var node) || IsString(node, out value);
    }

    private static bool OptionalText(JsonObject entry, string key, out string? value)
    {
        value = null;
        if (!entry.TryGetPropertyValue(key, out var node) || node is null)
        {
            return true;
        }

        var ok = IsString(node, out var text);
        value = text;
        return ok;
    }

    private static bool Flag(JsonObject entry, string key, out bool value)
    {
        value = true;
        if (!entry.TryGetPropertyValue(key, out var node))
        {
            return true;
        }

        var kind = node?.GetValueKind();
        value = kind == JsonValueKind.True;
        return kind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool Orca(JsonObject entry, out string? group)
    {
        group = null;
        if (!entry.TryGetPropertyValue("orca", out var node) || node is null)
        {
            return true;
        }

        if (node is not JsonObject orca || !Text(orca, "group", out var name))
        {
            return false;
        }

        group = name;
        return true;
    }

    private static bool IsString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node?.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        value = node.GetValue<string>();
        return true;
    }
}
