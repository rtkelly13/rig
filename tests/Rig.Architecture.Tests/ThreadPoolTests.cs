using System.Text.RegularExpressions;

namespace Rig.Architecture.Tests;

/// <summary>
/// Nothing in rig pushes work onto the thread pool by hand. <c>BannedSymbols.txt</c> lists the same calls, but
/// RS0030 is only a suggestion outside <c>src/Rig.Cli/Gh</c>, so this is the gate: it reads the C# sources, which
/// covers the test and web projects that this project does not reference.
/// </summary>
public sealed partial class ThreadPoolTests
{
    // Written in pieces so this file does not name what it forbids. Whitespace is allowed wherever C# allows it,
    // so a call split across lines is still a call.
    private const string Forbidden = @"\bTask\s*\.\s*(?:Run\s*[(<]|Factory\b)|\bTask" + @"Factory\b";

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
            .SelectMany(file => Calls(File.ReadAllText(file)).Select(call => $"{Path.GetRelativePath(root, file)}:{call}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Await the work directly, use Parallel.ForEachAsync for bounded fan-out, or a thread for blocking I/O:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("await Task" + ".Run(() => { });", "1: Task" + ".Run(")]
    [InlineData("await Task" + ".Run<int>(() => 1);", "1: Task" + ".Run<")]
    [InlineData("var task = Task\n    .Run(() => { });", "1: Task" + " .Run(")]
    [InlineData("\n_ = Task" + " . Factory.StartNew(() => { });", "2: Task" + " . Factory")]
    [InlineData("_ = new Task" + "Factory().StartNew(() => { });", "1: Task" + "Factory")]
    public void A_call_is_found_however_it_is_laid_out(string source, string expected) =>
        Assert.Equal([expected], Calls(source));

    [Theory]
    [InlineData("var watch = Stopwatch.StartNew();")]
    [InlineData("await MyTask.RunAsync();")]
    [InlineData("return Task.CompletedTask;")]
    public void Other_calls_are_left_alone(string source) =>
        Assert.Empty(Calls(source));

    // Each forbidden call as "line: what was written", its whitespace collapsed.
    private static IEnumerable<string> Calls(string source) =>
        Call().Matches(source).Select(match =>
            $"{source.AsSpan(0, match.Index).Count('\n') + 1}: {string.Join(' ', match.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}");

    [GeneratedRegex(Forbidden)]
    private static partial Regex Call();

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
