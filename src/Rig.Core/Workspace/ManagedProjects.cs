using System.Text.Json;
using Rig.Core.Config;

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

    // Entries are decoded one at a time: a misshapen entry is skipped rather than losing the whole registry, which is
    // what the Python reader did. The typed entry still comes from the source-generated context, so unknown keys such
    // as vercel or ports are ignored without reflection.
    private static List<ManagedRepoSpec> Decode(string path, bool retired)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("projects", out var projects)
                || projects.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var specs = new List<ManagedRepoSpec>();
            foreach (var element in projects.EnumerateArray())
            {
                if (Entry(element, retired) is { } spec)
                {
                    specs.Add(spec);
                }
            }

            return specs;
        }
    }

    private static ManagedRepoSpec? Entry(JsonElement element, bool retired)
    {
        ManagedProjectEntry? entry;
        try
        {
            entry = element.Deserialize(EstateConfigJsonContext.Default.ManagedProjectEntry);
        }
        catch (JsonException)
        {
            return null;
        }

        if (entry is null)
        {
            return null;
        }

        // The source generator leaves an absent property at its type's default, so an absent flag is not true and an
        // absent string is not "", and a flag of any other kind (including null) is the misshapen entry it always was.
        var enabled = entry.Enabled.ValueKind switch
        {
            JsonValueKind.Undefined => true,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        };
        return enabled is { } on
            ? new(
                Text(entry.Name),
                Text(entry.Repo),
                Text(entry.Description),
                Text(entry.Domain),
                Text(entry.Stack),
                entry.Branch,
                on && !retired,
                entry.Orca is { } orca ? Text(orca.Group) : null)
            : null;
    }

    private static string Text(string? value) => value ?? string.Empty;
}
