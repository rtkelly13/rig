using Rig.Core.Tests.Estate;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// A vault repository and a Drive copy for the Drive backup, in real git: a bare <c>origin.git</c>, a clone at
/// <c>vault-repo</c> whose <c>main</c> holds <c>Vault/</c>, and a placeholder Drive folder. Never a real account name.
/// </summary>
internal static class DriveVaults
{
    /// <summary>What <c>main</c> holds under <c>Vault/</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> Main = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Same.md"] = "same",
        ["Edited.md"] = "edited",
        ["Projects/With space.md"] = "spaced",
        ["Readme.txt"] = "not a note",
    };

    /// <summary>Builds the repository under <paramref name="temp"/> and a Drive copy holding <paramref name="drive"/>.</summary>
    /// <returns>The clone, its <c>Vault/</c> folder, and the Drive copy.</returns>
    public static async Task<(string Clone, string Vault, string Drive)> ArrangeAsync(TempDir temp, IReadOnlyDictionary<string, string> drive)
    {
        var origin = temp.Folder("origin.git");
        _ = await Repos.GitAsync(origin, "init", "-q", "--bare", "-b", "main");
        var clone = temp.Folder("vault-repo");
        _ = await Repos.GitAsync(clone, "init", "-q", "-b", "main");
        _ = await Repos.GitAsync(clone, "config", "user.name", "t");
        _ = await Repos.GitAsync(clone, "config", "user.email", "t@example.invalid");
        _ = await Repos.GitAsync(clone, "config", "commit.gpgsign", "false");
        Write(Path.Join(clone, "Vault"), Main);
        _ = await Repos.GitAsync(clone, "add", "Vault");
        _ = await Repos.GitAsync(clone, "commit", "-qm", "first");
        _ = await Repos.GitAsync(clone, "remote", "add", "origin", origin);
        _ = await Repos.GitAsync(clone, "push", "-q", "-u", "origin", "main");

        var driveVault = temp.Folder(Path.Join("Drive placeholder", "Vault"));
        Write(driveVault, drive);
        return (clone, Path.Join(clone, "Vault"), driveVault);
    }

    private static void Write(string root, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (relative, body) in files)
        {
            var path = Path.Join(root, relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }
    }
}
