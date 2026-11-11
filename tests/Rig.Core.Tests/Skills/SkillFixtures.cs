using Rig.Core.Skills;

namespace Rig.Core.Tests.Skills;

/// <summary>Skill folders and linkers for skills tests. Every source lives in a temp folder, never the real catalog.</summary>
public static class SkillFixtures
{
    /// <summary>Writes <c>SKILL.md</c> in <paramref name="relative"/> under <paramref name="root"/> and returns the skill's folder.</summary>
    public static string Write(string root, string relative, string? name = null, string? description = null)
    {
        var folder = Path.Join(root, relative);
        _ = Directory.CreateDirectory(folder);
        var front = "---\n" + (name is null ? string.Empty : $"name: {name}\n") + (description is null ? string.Empty : $"description: {description}\n") + "---\n";
        File.WriteAllText(Path.Join(folder, "SKILL.md"), front);
        return folder;
    }

    /// <summary>A catalog over the single source <paramref name="root"/>.</summary>
    public static SkillCatalog Catalog(string root, IReadOnlyList<string>? allowlist = null) =>
        new([new("test", root)], allowlist, windows: false);
}

/// <summary>Makes real links as rig does: a symlink, else on Windows without the privilege a junction.</summary>
public sealed class SymlinkLinker : IDirectoryLinker
{
    /// <inheritdoc/>
    public void Link(string link, string target) => new SystemDirectoryLinker().Link(link, target);
}

/// <summary>Refuses every link, as a Windows machine without the privilege or junction support would.</summary>
public sealed class RefusingLinker : IDirectoryLinker
{
    /// <inheritdoc/>
    public void Link(string link, string target) => throw new IOException("links refused");
}
