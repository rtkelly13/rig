using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;

namespace Rig.Core.Roots;

/// <summary>
/// The personal estate's scope, tracked in <c>config/estate-scopes.json</c>: the GitHub accounts whose repositories
/// are cloned straight into <c>personal/</c>, and the GitHub orgs whose repositories sit in a sibling folder of
/// <c>personal/</c> named for the org.
/// </summary>
/// <param name="Owners">GitHub accounts whose repositories are cloned into <c>personal/</c>.</param>
/// <param name="Orgs">GitHub orgs whose repositories sit in <c>&lt;code root&gt;/&lt;org&gt;</c>, spelled as the folder is.</param>
public sealed record EstateScopes(IReadOnlyList<string> Owners, IReadOnlyList<string> Orgs)
{
    /// <summary>The file under a rig checkout's <c>config/</c>.</summary>
    public const string FileName = "estate-scopes.json";

    /// <summary>
    /// The scopes in <paramref name="path"/>; empty when the file is missing. A file that is present but malformed
    /// throws: it is tracked, so that is a mistake to fix.
    /// </summary>
    public static EstateScopes Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new([], []);
        }

        var personal = (JsonNode.Parse(text) ?? throw new JsonException($"{path} is empty."))["personal"];
        return new(Names(personal?["owners"]), Names(personal?["orgs"]));
    }

    /// <summary>The org folders beside <paramref name="personalDir"/>, whether or not they exist yet.</summary>
    public IReadOnlyList<string> OrgDirs(string personalDir) =>
        [.. Orgs.Select(org => Path.Join(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(personalDir)), org))];

    /// <summary>Every folder whose children are personal clones: <paramref name="personalDir"/>, then each org folder.</summary>
    public IReadOnlyList<string> RepoParentDirs(string personalDir) => [personalDir, .. OrgDirs(personalDir)];

    /// <summary>The org folder for a GitHub <paramref name="owner"/> (any case), spelled as the folder is; null when it is not an estate org.</summary>
    public string? OrgFolder(string owner) => Orgs.FirstOrDefault(org => string.Equals(org, owner, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A managed project's location relative to the estate root, with forward slashes: <c>../&lt;org&gt;/&lt;name&gt;</c>
    /// for a repo an estate org owns, else <c>&lt;name&gt;</c>.
    /// </summary>
    /// <param name="name">The project's folder name.</param>
    /// <param name="repo">Its declared remote.</param>
    public string ProjectRelPath(string name, string repo) => OrgFolderOf(repo) is { } org ? $"../{org}/{name}" : name;

    /// <summary>The folder a managed project is expected to be cloned into, beside or under <paramref name="estateDir"/>.</summary>
    /// <param name="estateDir">The personal estate folder.</param>
    /// <param name="name">The project's folder name.</param>
    /// <param name="repo">Its declared remote.</param>
    public string ProjectDir(string estateDir, string name, string repo) =>
        OrgFolderOf(repo) is { } org ? Path.Join(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(estateDir)), org, name) : Path.Join(estateDir, name);

    /// <summary>The org folder a repo belongs in, or null when it belongs in the estate root.</summary>
    /// <param name="repo">A declared remote; anything not on GitHub belongs in the estate root.</param>
    public string? OrgFolderOf(string repo) => RepoSlug.FromRemote(repo) is { } slug ? OrgFolder(slug.Owner) : null;

    private static string[] Names(JsonNode? node) =>
        node is null ? [] : [.. node.AsArray().Select(item => item?.GetValue<string>() ?? throw new JsonException("A scope name is null."))];
}
