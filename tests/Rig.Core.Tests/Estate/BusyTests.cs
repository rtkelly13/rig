using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Estate;

public sealed class BusyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_snapshot_keeps_each_processs_working_folder_even_when_lsof_could_not_read_every_process()
    {
        var proc = new FakeProc().Answer(new Ran(1, "p12\nfcwd\nn/work/a\np13\nfcwd\nn/work/b c\n", "lsof: no pwd entry"), "lsof");

        var busy = await Busy.SnapshotAsync(proc, windows: false, Token);

        Assert.Equal(["/work/a", "/work/b c"], busy.Dirs);
        Assert.Equal(["lsof", "-n", "-d", "cwd", "-Fn"], proc.Requests.Single().Argv);
        Assert.Equal(Busy.LsofTimeout, proc.Requests.Single().Timeout);
    }

    [Theory]
    [InlineData(Ran.NotFound, "")]
    [InlineData(1, "")]
    public async Task No_lsof_or_a_failure_with_nothing_printed_is_unknown_not_idle(int code, string output)
    {
        var proc = new FakeProc().Answer(new Ran(code, output, "boom"), "lsof");

        var busy = await Busy.SnapshotAsync(proc, windows: false, Token);

        Assert.Null(busy.Dirs);
    }

    [Fact]
    public async Task A_timed_out_lsof_is_unknown_even_with_some_processes_printed()
    {
        var proc = new FakeProc().Answer(new Ran(Ran.TimedOut, "p12\nfcwd\nn/work/a\n", string.Empty), "lsof");

        var busy = await Busy.SnapshotAsync(proc, windows: false, Token);

        Assert.Null(busy.Dirs);
    }

    [Fact]
    public async Task Windows_is_unknown_without_asking()
    {
        var proc = new FakeProc();

        var busy = await Busy.SnapshotAsync(proc, windows: true, Token);

        Assert.Null(busy.Dirs);
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public void An_unknown_snapshot_refuses_unless_forced_and_a_busy_folder_refuses_even_forced()
    {
        using var temp = new TempDir();
        var tree = PathResolution.Resolve(temp.Folder("tree"));
        var busy = new Busy([Path.Join(tree, "src")]);

        Assert.Contains("lsof unavailable", Busy.Unknown.Refusal(tree, force: false), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Busy.Unknown.Refusal(tree, force: true));
        Assert.Contains("a process is working in it", busy.Refusal(tree, force: true), StringComparison.Ordinal);
        Assert.Equal(string.Empty, new Busy([temp.Path + "-elsewhere"]).Refusal(tree, force: false));
        Assert.True(Busy.InUse(tree, [tree]));
    }
}
