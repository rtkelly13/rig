using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Cli.Tests.Estate;

/// <summary>
/// <c>rig estate prune-caches</c> end to end: every tool is faked by name and the Claude temp root is a temp folder, so
/// nothing real is located, pruned or deleted.
/// </summary>
public sealed class PruneCachesCommandTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Home => Path.Join(_root.Path, "home");

    private async Task<(int Code, string Out, string Err)> RunAsync(IProc proc, string[] installed, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new EstateCommand(
            new EstateHost(_root.Path, Home, _root.Path),
            proc,
            new FakeGitHub(),
            _clock,
            name => installed.Contains(name) ? name : null,
            output,
            error);
        var code = await new RootCommand { command.Build() }.Parse(["estate", "prune-caches", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    private (FakeProc Proc, string Uv, string Nuget) Located()
    {
        var uv = _root.Folder("uv-cache");
        File.WriteAllBytes(Path.Join(uv, "a.bin"), new byte[2048]);
        var nuget = _root.Folder("http-cache");
        File.WriteAllBytes(Path.Join(nuget, "b.dat"), new byte[1024]);
        var proc = new FakeProc()
            .Answer(new Ran(0, uv + "\n", string.Empty), "uv", "cache", "dir")
            .Answer(new Ran(1, string.Empty, "spawn ENOEXEC"), "pnpm", "store", "path")
            .Answer(new Ran(0, $"http-cache: {nuget}\n", string.Empty), "dotnet", "nuget", "locals", "http-cache", "--list");
        return (proc, uv, nuget);
    }

    [Fact]
    public async Task A_dry_run_prints_each_category_and_what_is_known_to_free_and_exits_1_on_a_failure()
    {
        var (proc, uv, nuget) = Located();
        var temp = Path.Join(_root.Path, "no-claude");

        var (code, output, error) = await RunAsync(proc, ["uv", "pnpm", "dotnet"], "--claude-temp", temp);

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, error);
        var lines = output.Split('\n');
        Assert.Equal("Caches (dry run — pass --apply)", lines[0]);
        Assert.Matches(@"^category\s+status\s+size\s+frees\s+detail$", lines[1]);
        Assert.Matches($@"^uv cache\s+would run\s+2\.0KB\s+\?\s+{System.Text.RegularExpressions.Regex.Escape(uv)}$", lines[2]);
        Assert.Matches(@"^pnpm store\s+failed\s+\?\s+\?\s+cannot locate the cache: spawn ENOEXEC$", lines[3]);
        Assert.Matches($@"^nuget http-cache\s+would run\s+1\.0KB\s+1\.0KB\s+{System.Text.RegularExpressions.Regex.Escape(nuget)}$", lines[4]);
        Assert.Contains($"{temp} does not exist", lines[5], StringComparison.Ordinal);
        Assert.Matches(@"^docker build cache\s+skipped\s+\?\s+\?\s+docker not found on PATH$", lines[6]);
        Assert.Equal(["", "Known to free 1.0KB", ""], lines[7..]);
        Assert.DoesNotContain(proc.Requests, r => r.Argv.Contains("prune") || r.Argv.Contains("--clear"));
    }

    [Fact]
    public async Task Json_writes_every_field_python_wrote_with_nulls_for_unknown_sizes()
    {
        var (proc, uv, _) = Located();
        var temp = Path.Join(_root.Path, "no-claude");

        var (code, output, _) = await RunAsync(proc, ["uv", "pnpm", "dotnet"], "--json", "--claude-temp", temp, "--idle-days", "3");

        Assert.Equal(1, code);
        Assert.Contains("idle 3+ days", output, StringComparison.Ordinal);
        var items = JsonNode.Parse(output)!.AsArray();
        Assert.Equal(5, items.Count);
        var first = items[0]!.AsObject();
        Assert.Equal(["category", "command", "status", "reason", "size_before", "freed"], first.Select(p => p.Key));
        Assert.Equal("uv cache", (string?)first["category"]);
        Assert.Equal("uv cache prune", (string?)first["command"]);
        Assert.Equal("would run", (string?)first["status"]);
        Assert.Equal(uv, (string?)first["reason"]);
        Assert.Equal(2048, (long?)first["size_before"]);
        Assert.Null(first["freed"]);
        Assert.Equal($"delete session dirs idle 3+ days under {temp}", (string?)items[3]!["command"]);
    }

    [Fact]
    public async Task Apply_with_nothing_installed_frees_nothing_and_exits_0()
    {
        var proc = new FakeProc();

        var (code, output, _) = await RunAsync(proc, [], "--apply", "--claude-temp", Path.Join(_root.Path, "no-claude"));

        Assert.Equal(0, code);
        Assert.StartsWith("Caches\n", output, StringComparison.Ordinal);
        Assert.EndsWith("\nFreed 0B\n", output, StringComparison.Ordinal);
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Apply_removes_idle_claude_sessions_under_a_home_relative_root()
    {
        var session = _root.Folder(Path.Join("home", "ct", "project", "0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d"));
        await File.WriteAllBytesAsync(Path.Join(session, "out.txt"), new byte[100], Token);
        var old = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Join(session, "out.txt"), old);
        Directory.SetLastWriteTimeUtc(session, old);

        var (code, output, _) = await RunAsync(new FakeProc(), [], "--apply", "--json", "--claude-temp", "~/ct");

        Assert.Equal(0, code);
        var claude = JsonNode.Parse(output)!.AsArray()[3]!;
        Assert.Equal("done", (string?)claude["status"]);
        Assert.Equal("1 session dir(s) removed", (string?)claude["reason"]);
        Assert.Equal(100, (long?)claude["freed"]);
        Assert.False(Directory.Exists(session));
    }
}
