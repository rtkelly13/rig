namespace Rig.Core.Tests;

/// <summary>Scratch paths under the checkout's gitignored <c>temp/</c>, where AGENTS.md keeps every temporary file.</summary>
public static class RepoTemp
{
    /// <summary>The checkout's <c>temp/</c> folder: beside the <c>rig.slnx</c> above the test output.</summary>
    public static string Root { get; } = Find();

    /// <summary>A fresh path under <see cref="Root"/>, named after <paramref name="prefix"/>; nothing is created.</summary>
    public static string NewPath(string prefix) => Path.Combine(Root, "tests", $"{prefix}-{Guid.NewGuid():N}");

    /// <summary>
    /// A fresh folder to build a vault in, whose path holds none of the substrings the vault code matches against a
    /// note's full path (it saw what Python saw, the vault's path included). A checkout under, say, <c>~/Resources</c>
    /// falls back to the system temp folder; the test is skipped if that holds one too. Nothing is created.
    /// </summary>
    public static string NewVaultParent(string prefix)
    {
        foreach (var candidate in new[] { NewPath(prefix), Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}") })
        {
            if (!VaultPathTriggers.Any(t => candidate.Contains(t, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }

        Assert.Skip($"Both the checkout and the temp folder lie under a path holding one of: {string.Join(", ", VaultPathTriggers)}.");
        return string.Empty;
    }

    // What VaultLinter and VaultRepair look for in a note's full path; Areas and Projects are only matched relatively.
    private static readonly string[] VaultPathTriggers = [".git", ".obsidian", "Archive", "Resources", "Social Media Posts"];

    private static string Find()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "rig.slnx")))
            {
                return Path.Combine(folder.FullName, "temp");
            }
        }

        throw new InvalidOperationException("rig.slnx is not above the test output folder.");
    }
}
