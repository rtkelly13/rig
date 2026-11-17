using System.CommandLine;
using Rig.Cli.ReviewQueue;
using Rig.Core.GitHub;
using Rig.Core.ReviewQueue;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.ReviewQueue;

public sealed class TriageCommandTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeQueueSource _source = new();
    private readonly FakeProc _proc = new();
    private readonly StringWriter _output = new() { NewLine = "\n" };
    private readonly StringWriter _error = new() { NewLine = "\n" };
    private TriageHost Host => new(Path.Join(_temp.Path, "rig.db"), Path.Join(_temp.Path, "temp", "triage", "queue.md"), Path.Join(_temp.Path, "managed.json"));

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Refresh_and_verify_run_from_the_csharp_front_door_without_python()
    {
        _source.Items.Add(Item());
        Assert.Equal(0, await Run("triage", "refresh"));
        Assert.Equal($"Tracked 1 items; queue: {Host.Queue}\n", _output.ToString());
        Assert.Empty(_error.ToString());
        Assert.Single(new QueueStore(Host.Database).Read());
        var before = await File.ReadAllBytesAsync(Host.Queue, TestContext.Current.CancellationToken);
        var stamp = File.GetLastWriteTimeUtc(Host.Queue);
        Assert.Equal(0, await Run("triage", "verify"));
        Assert.Equal($"Valid triage queue: {Host.Queue}\n", _output.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(Host.Queue, TestContext.Current.CancellationToken));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Host.Queue));
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Overrides_write_only_the_requested_database_and_output()
    {
        _source.Items.Add(Item());
        var db = Path.Join(_temp.Path, "override", "other.db");
        var queue = Path.Join(_temp.Path, "override", "other.md");
        var registry = Path.Join(_temp.Path, "override", "managed.json");
        Assert.Equal(0, await Run("triage", "refresh", "--db", db, "--output", queue, "--config", registry));
        Assert.Single(new QueueStore(db).Read());
        Assert.Contains($"repositories:{registry}", _source.Calls);
        Assert.False(File.Exists(Host.Database));
        Assert.False(File.Exists(Host.Queue));
        Assert.Equal(0, await Run("triage", "verify", queue));
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Verify_is_offline_and_never_initializes_a_database()
    {
        var path = _temp.File("queue.md");
        await File.WriteAllTextAsync(path, QueueMarkdown.Project([Item()]), TestContext.Current.CancellationToken);
        Assert.Equal(0, await Run("triage", "verify", path));
        Assert.False(File.Exists(Host.Database));
        Assert.Empty(_source.Calls);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Invalid_markdown_is_an_error_on_stderr_and_is_not_repaired()
    {
        var path = _temp.File("queue.md");
        const string broken = "My notes, with no generated block";
        await File.WriteAllTextAsync(path, broken, TestContext.Current.CancellationToken);
        Assert.Equal(1, await Run("triage", "verify", path));
        Assert.Empty(_output.ToString());
        Assert.StartsWith("Invalid triage queue:", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(broken, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(1, await Run("triage", "verify", Path.Join(_temp.Path, "missing.md")));
        Assert.False(File.Exists(Host.Database));
    }

    [Fact]
    public async Task Failed_refresh_reports_failure_and_preserves_the_previous_queue()
    {
        _source.Items.Add(Item());
        Assert.Equal(0, await Run("triage", "refresh"));
        var before = await File.ReadAllBytesAsync(Host.Queue, TestContext.Current.CancellationToken);
        _source.Items.Clear();
        _source.Items.Add(Item() with { Title = "Not committed" });
        _source.FailAt = "fetch:owner/repo:issue";
        Assert.Equal(1, await Run("triage", "refresh"));
        Assert.Empty(_output.ToString());
        Assert.Equal("Triage refresh failed: GitHub unavailable\n", _error.ToString());
        Assert.Equal("PR", new QueueStore(Host.Database).Read()[0].Item.Title);
        Assert.Equal(before, await File.ReadAllBytesAsync(Host.Queue, TestContext.Current.CancellationToken));
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Help_and_parse_errors_are_owned_by_csharp()
    {
        Assert.Equal(2, await Run("triage"));
        Assert.Equal("rig: Required command was not provided.\nRun 'rig --help' for the commands.\n", _error.ToString());
        Assert.Equal(0, await Run("triage", "--help"));
        Assert.Contains("refresh", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("verify", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await Run("triage", "verify", "--unknown"));
        Assert.Equal(1, await Run("triage", "verify", "--", "--unknown"));
        Assert.StartsWith("Invalid triage queue:", _error.ToString(), StringComparison.Ordinal);
        Assert.Empty(_proc.Requests);
        Assert.Empty(_source.Calls);
    }

    private async Task<int> Run(params string[] args)
    {
        _output.GetStringBuilder().Clear();
        _error.GetStringBuilder().Clear();
        var command = new TriageCommand(Host, _source, _output, _error);
        return await new RigApp(new FakeGitHub(), _ => Task.FromResult<RepoSlug?>(null),
            _output, _error, [command.Build]).RunAsync(args, TestContext.Current.CancellationToken);
    }

    private static QueueItem Item() => new("owner/repo", 1, "pr", "PR", "OPEN", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", new string('a', 40), Readiness: 0);
}
