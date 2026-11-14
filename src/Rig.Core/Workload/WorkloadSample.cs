namespace Rig.Core.Workload;

/// <summary>
/// One sample: one JSON line in the day's file (<see cref="WorkloadPaths"/>). Field names are written in snake case;
/// docs/workload.md documents the shape. Memory is in MB (MiB), rounded.
/// </summary>
/// <param name="V">The shape's version, 1.</param>
/// <param name="Ts">When it was taken, UTC, to the second.</param>
/// <param name="Host">The machine's name.</param>
/// <param name="Os"><c>windows</c> or <c>macos</c>.</param>
/// <param name="Ram">Installed and free memory.</param>
/// <param name="Processes">Every process, summed.</param>
/// <param name="Apps">Memory by owning app, largest first, then <c>other</c>.</param>
/// <param name="Claude">Claude Code sessions.</param>
/// <param name="Large">Single processes over the large threshold, largest first.</param>
/// <param name="Bursts">Names with more live copies than the burst threshold.</param>
public sealed record WorkloadSample(
    int V,
    DateTime Ts,
    string Host,
    string Os,
    RamUse Ram,
    ProcessTotals Processes,
    IReadOnlyList<AppUse> Apps,
    SessionUse Claude,
    IReadOnlyList<LargeProcess> Large,
    IReadOnlyList<Burst> Bursts);

/// <summary>Installed and free memory.</summary>
/// <param name="TotalMb">Installed.</param>
/// <param name="FreeMb">Available for new work.</param>
public sealed record RamUse(long TotalMb, long FreeMb);

/// <summary>Every process, summed.</summary>
/// <param name="Count">How many.</param>
/// <param name="PrivateMb">Private memory.</param>
/// <param name="WorkingSetMb">Working set.</param>
public sealed record ProcessTotals(int Count, long PrivateMb, long WorkingSetMb);

/// <summary>The processes one app owns.</summary>
/// <param name="App">The app, or <c>other</c>: processes no app owns plus apps past the top ten.</param>
/// <param name="Processes">How many.</param>
/// <param name="PrivateMb">Their private memory.</param>
/// <param name="WorkingSetMb">Their working set.</param>
public sealed record AppUse(string App, int Processes, long PrivateMb, long WorkingSetMb);

/// <summary>Minimum, median and maximum.</summary>
/// <param name="Min">The smallest.</param>
/// <param name="Median">The middle one (the lower middle for an even count).</param>
/// <param name="Max">The largest.</param>
public sealed record Spread(long Min, long Median, long Max);

/// <summary>The sessions app's sessions (Claude Code).</summary>
/// <param name="Sessions">How many top-level session processes are running.</param>
/// <param name="SessionMb">Each session process's own private memory; null with no sessions.</param>
/// <param name="TreeMb">Each session with everything it started (shells, MCP servers, builds, tests); null with no sessions.</param>
/// <param name="Children">Processes running under sessions, by name, most first (top ten).</param>
public sealed record SessionUse(int Sessions, Spread? SessionMb, Spread? TreeMb, IReadOnlyDictionary<string, int> Children);

/// <summary>One process over the large threshold.</summary>
/// <param name="Pid">Its id.</param>
/// <param name="Name">Its executable name.</param>
/// <param name="App">The app that owns it, or <c>other</c>.</param>
/// <param name="PrivateMb">Its private memory.</param>
/// <param name="WorkingSetMb">Its working set.</param>
/// <param name="Started">When it started, UTC.</param>
/// <param name="Command">Its command line, redacted and cut (<see cref="CommandLines.Safe"/>); empty when unreadable.</param>
public sealed record LargeProcess(int Pid, string Name, string App, long PrivateMb, long WorkingSetMb, DateTime? Started, string Command);

/// <summary>A name with many live copies.</summary>
/// <param name="Name">The executable name.</param>
/// <param name="Count">How many are running.</param>
/// <param name="App">The app that owns most of them, or <c>other</c>.</param>
/// <param name="PrivateMb">Their private memory together.</param>
public sealed record Burst(string Name, int Count, string App, long PrivateMb);
