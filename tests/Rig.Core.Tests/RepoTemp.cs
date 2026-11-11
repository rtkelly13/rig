namespace Rig.Core.Tests;

/// <summary>Scratch paths under the checkout's gitignored <c>temp/</c>, where AGENTS.md keeps every temporary file.</summary>
public static class RepoTemp
{
    /// <summary>The checkout's <c>temp/</c> folder: beside the <c>rig.slnx</c> above the test output.</summary>
    public static string Root { get; } = Find();

    /// <summary>A fresh path under <see cref="Root"/>, named after <paramref name="prefix"/>; nothing is created.</summary>
    public static string NewPath(string prefix) => Path.Combine(Root, "tests", $"{prefix}-{Guid.NewGuid():N}");

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
