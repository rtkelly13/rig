using System.Buffers.Binary;
using System.Text;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workload;

namespace Rig.Core.Tests.Workload;

/// <summary>Reading the process table: the Windows kernel buffer, decoded from built bytes, and macOS's ps through a fake.</summary>
public sealed class ProcessTableTests
{
    private const long Base = 0x1000_0000;
    private const int Entry = 256;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_kernel_buffer_decodes_to_rows()
    {
        var started = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        var buffer = new byte[(Entry * 3) + 64];
        // Idle (pid 0, no name), then claude.exe, then a last entry whose name points outside the buffer.
        Write(buffer, 0, next: Entry, pid: 0, parent: 0, created: 0, name: null, nameAt: 0, privateBytes: 0, workingSet: 8192);
        Write(buffer, Entry, next: Entry, pid: 42, parent: 7, created: started.ToFileTimeUtc(), name: "claude.exe", nameAt: Entry * 3, privateBytes: 800L << 20, workingSet: 400L << 20);
        Write(buffer, Entry * 2, next: 0, pid: 9, parent: 42, created: 0, name: "gone", nameAt: buffer.Length + 100, privateBytes: 1, workingSet: 2);

        var rows = SystemProcessInformation.Parse(buffer, Base);

        Assert.Equal(
            [
                new ProcessRow(0, 0, "Idle", string.Empty, 0, 8192, null),
                new ProcessRow(42, 7, "claude.exe", string.Empty, 800L << 20, 400L << 20, started),
                new ProcessRow(9, 42, string.Empty, string.Empty, 1, 2, null),
            ],
            rows);
        Assert.Empty(SystemProcessInformation.Parse(new byte[10], Base));
    }

    [Fact]
    public void A_unicode_string_is_read_only_inside_its_buffer()
    {
        var buffer = new byte[64];
        var text = Encoding.Unicode.GetBytes("a b");
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)text.Length);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), Base + 16);
        text.CopyTo(buffer, 16);

        Assert.Equal("a b", SystemProcessInformation.UnicodeString(buffer, Base));
        Assert.Equal(string.Empty, SystemProcessInformation.UnicodeString(buffer, Base + 100));
        Assert.Equal(string.Empty, SystemProcessInformation.UnicodeString(new byte[8], Base));
    }

    [Fact]
    public async Task Macos_reads_ps_sysctl_and_vm_stat()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
        var proc = new FakeProc()
            .Answer(
                new Ran(0, "    1     0  12000 Wed Nov 11 08:00:00 2026     /sbin/launchd\n  420     1 819200 Wed Nov 11 09:05:07 2026     /Users/me/.local/share/claude/versions/2.1.3\n  421   420   2048 Thu Nov  5 10:00:00 2026     zsh\nnot a row\n", string.Empty),
                MacProcessTable.Ps,
                "-axo")
            .Answer(new Ran(0, "68719476736\n", string.Empty), MacProcessTable.Sysctl)
            .Answer(
                new Ran(0, "Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages free:                               1000.\nPages active:                             5000.\nPages inactive:                           300.\nPages speculative:                        24.\n", string.Empty),
                MacProcessTable.VmStat)
            .Answer(new Ran(1, "  420 /Users/me/.local/bin/claude --resume\n", string.Empty), MacProcessTable.Ps, "-o");
        var table = new MacProcessTable(proc, zone);

        var snapshot = await table.SnapshotAsync(Ct);
        var commands = await table.CommandLinesAsync([420, 999], Ct);

        Assert.Equal(new MemoryStatus(68719476736, 1324L * 16384), snapshot.Memory);
        Assert.Equal(
            [
                new ProcessRow(1, 0, "launchd", "/sbin/launchd", 12000L * 1024, 12000L * 1024, new DateTime(2026, 11, 11, 7, 0, 0, DateTimeKind.Utc)),
                new ProcessRow(420, 1, "2.1.3", "/Users/me/.local/share/claude/versions/2.1.3", 819200L * 1024, 819200L * 1024, new DateTime(2026, 11, 11, 8, 5, 7, DateTimeKind.Utc)),
                new ProcessRow(421, 420, "zsh", string.Empty, 2048L * 1024, 2048L * 1024, new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc)),
            ],
            snapshot.Processes);
        Assert.Equal(new Dictionary<int, string> { [420] = "/Users/me/.local/bin/claude --resume" }, commands);
        Assert.All(proc.Requests, request => Assert.Equal("C", request.Environment!["LC_ALL"]));
        Assert.Contains(proc.Requests, request => request.Argv.SequenceEqual([MacProcessTable.Ps, "-o", "pid=,command=", "-p", "420,999"]));
        Assert.Empty(await table.CommandLinesAsync([], Ct));
    }

    [Fact]
    public async Task Macos_fails_when_ps_does()
    {
        var table = new MacProcessTable(new FakeProc(), TimeZoneInfo.Utc);

        await Assert.ThrowsAsync<ProcException>(() => table.SnapshotAsync(Ct));
    }

    [Fact]
    public void Vm_stat_without_a_page_size_assumes_four_kilobytes()
    {
        Assert.Equal(10 * 4096, MacProcessTable.Available("Pages free: 10.\n"));
        Assert.Null(MacProcessTable.ParseTable("  5  1  10 Xyz Abc 99 99:99:99 2026  odd\n", TimeZoneInfo.Utc)[0].Started);
    }

    private static void Write(
        byte[] buffer, int at, int next, long pid, long parent, long created, string? name, int nameAt, long privateBytes, long workingSet)
    {
        var entry = buffer.AsSpan(at);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)next);
        BinaryPrimitives.WriteInt64LittleEndian(entry[32..], created);
        BinaryPrimitives.WriteInt64LittleEndian(entry[80..], pid);
        BinaryPrimitives.WriteInt64LittleEndian(entry[88..], parent);
        BinaryPrimitives.WriteInt64LittleEndian(entry[144..], workingSet);
        BinaryPrimitives.WriteInt64LittleEndian(entry[184..], privateBytes);
        if (name is not null)
        {
            var text = Encoding.Unicode.GetBytes(name);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[56..], (ushort)text.Length);
            BinaryPrimitives.WriteInt64LittleEndian(entry[64..], Base + nameAt);
            if (nameAt + text.Length <= buffer.Length)
            {
                text.CopyTo(buffer, nameAt);
            }
        }
    }
}
