namespace Rig.Architecture.Tests;

/// <summary>
/// Nothing in rig pushes work onto the thread pool by hand. <c>BannedSymbols.txt</c> lists the same calls, but
/// RS0030 is only a suggestion outside <c>src/Rig.Cli/Gh</c>, so this is the gate: it reads the C# sources, which
/// covers the test and web projects that this project does not reference.
/// </summary>
public sealed class ThreadPoolTests
{
    // Written in pieces so this file does not name what it forbids.
    private static readonly string[] Forbidden = ["Task" + ".Run(", "Task" + ".Run<", "Task" + ".Factory", "TaskFactory" + "."];

    private static readonly string[] SourceFolders = ["src", "tests", "tools", "scripts"];

    [Fact]
    public void No_source_file_starts_work_on_the_thread_pool_by_hand()
    {
        var root = RepoRoot();
        var offenders = SourceFolders
            .Select(folder => Path.Join(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(file => !Generated(Path.GetRelativePath(root, file)))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (line, number: index + 1))
                .Where(entry => Forbidden.Any(call => entry.line.Contains(call, StringComparison.Ordinal)))
                .Select(entry => $"{Path.GetRelativePath(root, file)}:{entry.number}: {entry.line.Trim()}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Await the work directly, use Parallel.ForEachAsync for bounded fan-out, or a thread for blocking I/O:\n" + string.Join('\n', offenders));
    }

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
