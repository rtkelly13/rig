using Rig.Core.Workload;

namespace Rig.Core.Tests.Fakes;

/// <summary>An <see cref="IProcessTable"/> that returns a fixed snapshot and records which command lines were asked for.</summary>
public sealed class FakeProcessTable : IProcessTable
{
    /// <summary>The processes the snapshot holds.</summary>
    public List<ProcessRow> Processes { get; } = [];

    /// <summary>The snapshot's memory status.</summary>
    public MemoryStatus Memory { get; set; } = new(64L << 30, 32L << 30);

    /// <summary>Command lines by pid.</summary>
    public Dictionary<int, string> Commands { get; } = [];

    /// <summary>Thrown by <see cref="SnapshotAsync"/> when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>The pids each <see cref="CommandLinesAsync"/> call asked for.</summary>
    public List<IReadOnlyCollection<int>> Asked { get; } = [];

    /// <summary>Adds a process and returns this table.</summary>
    public FakeProcessTable Add(int pid, int parent, string name, long privateMb, long workingSetMb = 0, string path = "", DateTime? started = null)
    {
        Processes.Add(new ProcessRow(pid, parent, name, path, privateMb << 20, workingSetMb << 20, started));
        return this;
    }

    /// <inheritdoc />
    public Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        Failure is null ? Task.FromResult(new ProcessSnapshot([.. Processes], Memory)) : Task.FromException<ProcessSnapshot>(Failure);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, string>> CommandLinesAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken = default)
    {
        Asked.Add(pids);
        IReadOnlyDictionary<int, string> found = pids.Where(Commands.ContainsKey).ToDictionary(pid => pid, pid => Commands[pid]);
        return Task.FromResult(found);
    }
}
