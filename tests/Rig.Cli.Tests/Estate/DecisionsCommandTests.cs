using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Cli.Tests.Estate;

/// <summary>
/// <c>rig estate decisions</c> end to end over a real clone in a temp folder. The feed goes to a temp path and GitHub is
/// faked, so neither the real feed nor the real API is touched.
/// </summary>
public sealed class DecisionsCommandTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow.AddDays(8));

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Home => Path.Join(_root.Path, "home");

    [Fact]
    public async Task The_feed_it_writes_is_the_json_it_prints()
    {
        var ghost = await GhostAsync();
        var feed = Path.Join(_root.Path, "out", "decisions.json");
        var missing = Path.Join(_root.Path, "absent");

        var (code, output, error) = await RunAsync("--root", Path.GetDirectoryName(Path.GetDirectoryName(ghost))!, "--root", missing, "--write", feed, "--json");

        Assert.Equal(0, code);
        Assert.Equal(output, (await File.ReadAllTextAsync(feed, Token)).ReplaceLineEndings("\n"));
        var only = Assert.Single((JsonNode.Parse(output) as JsonArray)!)!;
        Assert.Equal(("locked-path", ghost, 0), ((string)only["kind"]!, (string)only["path"]!, (int)only["size_bytes"]!));
        Assert.Equal($"skipping {PathResolution.Resolve(missing)}: not a directory\nwrote 1 decision(s) to {feed}\n", error);
    }

    [Fact]
    public async Task The_table_shows_each_decision_with_its_command()
    {
        var ghost = await GhostAsync();

        var (code, output, _) = await RunAsync("--root", Path.GetDirectoryName(Path.GetDirectoryName(ghost))!);

        Assert.Equal(0, code);
        var lines = output.Split('\n');
        Assert.Equal("1 decision(s)", lines[0]);
        Assert.Matches(@"^kind\s+repo\s+path\s+size\s+reason\s+command$", lines[1]);
        Assert.Matches($@"^locked-path\s+app\s+{System.Text.RegularExpressions.Regex.Escape(ghost)}\s+0B\s+git no longer tracks it", lines[2]);
        Assert.EndsWith($"cmd /c rmdir /s /q \"{ghost}\"", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_roots_are_personal_then_existing_org_and_work_folders()
    {
        var scopes = Path.Join(_root.Path, "estate-scopes.json");
        File.WriteAllText(scopes, """{"personal": {"owners": ["me"], "orgs": ["labs", "gone"]}}""");
        var config = Path.Join(_root.Path, "config.json");
        File.WriteAllText(config, """{"tools": {"estate": {"work_dirs": ["acme", " ", "~/far", "missing"]}}}""");
        var personal = PathResolution.Resolve(_root.Folder("personal"));
        _ = _root.Folder("labs");
        _ = _root.Folder("acme");
        var far = Directory.CreateDirectory(Path.Join(Home, "far")).FullName;

        var roots = Command(new FakeProc(), scopes, config).DefaultRoots();

        Assert.Equal([personal, Path.Join(Path.GetDirectoryName(personal), "labs"), Path.Join(Path.GetDirectoryName(personal), "acme"), far], roots);
    }

    private DecisionsCommand Command(IProc proc, string scopes = "", string config = "") =>
        new(new EstateHost(_root.Path, Home, _root.Path, scopes, config), new Git(proc, _root.Path), new FakeGitHub(), _clock, windows: false, TextWriter.Null, TextWriter.Null);

    // A clone at <root>/code/app whose .worktrees/ghost is a folder git does not track.
    private async Task<string> GhostAsync()
    {
        var clone = PathResolution.Resolve(_root.Folder(Path.Join("code", "app")));
        var ran = await new SystemProc().RunAsync(new ProcRequest(["git", "init", "-q", "-b", "main"]) { WorkingDirectory = clone }, Token);
        Assert.True(ran.Ok, ran.Err);
        return Directory.CreateDirectory(Path.Join(clone, ".worktrees", "ghost")).FullName;
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new EstateCommand(new EstateHost(_root.Path, Home, _root.Path), new SystemProc(), new FakeGitHub(), _clock, _ => null, output, error);
        var code = await new RootCommand { command.Build() }.Parse(["estate", "decisions", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }
}
