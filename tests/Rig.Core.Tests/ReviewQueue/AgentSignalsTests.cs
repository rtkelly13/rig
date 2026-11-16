using System.Text;
using Rig.Core.Processes;
using Rig.Core.ReviewQueue;

namespace Rig.Core.Tests.ReviewQueue;

/// <summary>
/// The scanner's estate wiring: naming the running checkout is a git call, so it is awaited rather than blocked on,
/// and a failure settles the roots while a cancelled call reaches the caller.
/// </summary>
public sealed class AgentSignalsTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ForEstate_asks_git_for_the_running_checkouts_origin_and_falls_back_when_it_fails()
    {
        var proc = OriginProc.Answering(new Ran(128, string.Empty, "fatal: not a git repository"));

        using var signals = await AgentSignals.ForEstateAsync(
            Registry(),
            _temp.Folder("personal"),
            new Git(proc, _temp.Path),
            TestContext.Current.CancellationToken);

        Assert.Equal(["git", "config", "--get", "remote.origin.url"], Assert.Single(proc.Requests));
        Assert.Empty(await signals.AllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_timed_out_origin_lookup_falls_back_instead_of_failing_the_scanner()
    {
        var proc = OriginProc.Throwing(new TimeoutException("git did not answer"));

        using var signals = await AgentSignals.ForEstateAsync(
            Registry(),
            _temp.Folder("personal"),
            new Git(proc, _temp.Path),
            TestContext.Current.CancellationToken);

        Assert.Empty(await signals.AllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_origin_lookup_surfaces_to_the_caller()
    {
        var proc = OriginProc.Throwing(new OperationCanceledException());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => AgentSignals.ForEstateAsync(
            Registry(),
            _temp.Folder("personal"),
            new Git(proc, _temp.Path),
            cancelled.Token));

        Assert.Equal(cancelled.Token, Assert.Single(proc.Tokens));
    }

    // A registry one level inside a checkout, so the scanner names the running checkout as it does under git.
    private string Registry()
    {
        var path = Path.Join(_temp.Path, "config", "managed-projects.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"projects":[{"name":"rig","repo":"git@github.com:owner/rig.git"}]}""", Encoding.UTF8);
        return path;
    }

    // Answers every git call with one exit, or fails it outright, recording what git was asked and with which token.
    private sealed class OriginProc(Exception? failure, Ran answer) : IProc
    {
        public List<IReadOnlyList<string>> Requests { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public static OriginProc Answering(Ran answer) => new(null, answer);

        public static OriginProc Throwing(Exception failure) => new(failure, new Ran(0, string.Empty, string.Empty));

        public Ran Run(ProcRequest request) => failure is null ? answer : throw failure;

        public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Argv);
            Tokens.Add(cancellationToken);
            return failure is null ? Task.FromResult(answer) : Task.FromException<Ran>(failure);
        }
    }
}
