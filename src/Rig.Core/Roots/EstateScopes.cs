using System.Text.Json;
using System.Text.Json.Nodes;

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

    private static string[] Names(JsonNode? node) =>
        node is null ? [] : [.. node.AsArray().Select(item => item?.GetValue<string>() ?? throw new JsonException("A scope name is null."))];
}
