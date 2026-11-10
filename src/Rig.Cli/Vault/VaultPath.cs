namespace Rig.Cli.Vault;

/// <summary>
/// Where vault maintenance points. The vault exists twice: the Google Drive copy that Obsidian opens and the phone
/// edits, and the git copy that is the record. Maintenance targets the git copy, so repairs there are reviewable and
/// committable; the two converge through the <c>vault-reconcile</c> skill, not through continuous sync.
/// </summary>
internal static class VaultPath
{
    /// <summary>The variables that override the vault, in order; <c>TVS_VAULT_PATH</c> is the legacy name.</summary>
    public static readonly IReadOnlyList<string> Variables = ["VAULT_PATH", "TVS_VAULT_PATH"];

    /// <summary>The git copy: the first override that is set, else <c>~/code/personal/obsidian-vault/Vault</c>.</summary>
    public static string Resolve(Func<string, string?> environment, string home)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var variable in Variables)
        {
            var value = environment(variable);
            if (!string.IsNullOrEmpty(value))
            {
                return ExpandHome(value, home);
            }
        }

        return Path.Combine(home, "code", "personal", "obsidian-vault", "Vault");
    }

    /// <summary>Expands a leading <c>~</c> to <paramref name="home"/>, as Python's <c>expanduser</c> does.</summary>
    public static string ExpandHome(string path, string home)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path == "~")
        {
            return home;
        }

        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..])
            : path;
    }
}
