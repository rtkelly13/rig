namespace Rig.Architecture.Tests;

/// <summary>
/// ADR 0025: Paket has no per-repo default references, so the analyzer that Directory.Build.props used to add to every
/// project must be listed in each project's <c>paket.references</c>. A project that forgets it builds without RS0030.
/// </summary>
public sealed class PaketReferencesTests
{
    private const string Analyzer = "Microsoft.CodeAnalysis.BannedApiAnalyzers";

    private static readonly string[] ProjectFolders = ["src", "tests", "tools", "experiments"];

    [Fact]
    public void Every_project_references_the_banned_api_analyzer()
    {
        var root = RepoRoot();
        var offenders = ProjectFolders
            .Select(folder => Path.Join(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .Where(project => !Generated(Path.GetRelativePath(root, project)))
            .Where(project => !Lines(Path.Join(Path.GetDirectoryName(project), "paket.references")).Contains(Analyzer, StringComparer.Ordinal))
            .Select(project => Path.GetRelativePath(root, project))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0, $"Add {Analyzer} to the paket.references beside:\n" + string.Join('\n', offenders));
    }

    private static string[] Lines(string references) =>
        File.Exists(references) ? [.. File.ReadAllLines(references).Select(line => line.Trim())] : [];

    private static bool Generated(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Join(folder.FullName, "rig.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("rig.slnx is not above the test output folder.");
    }
}
