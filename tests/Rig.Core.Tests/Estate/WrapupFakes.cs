using Rig.Core.Estate;

namespace Rig.Core.Tests.Estate;

/// <summary>The sessions, worktrees and terminals <c>estate wrapup</c>'s tests plan over: a code root at <c>/code</c>, Orca never reached.</summary>
internal static class WrapupFixtures
{
    public static readonly string Root = Path.GetFullPath("/code");

    public static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public static readonly GitFacts Clean = new(false, 0, false);

    public static readonly WrapupRepo[] Repos =
    [
        new("rig-id", Under("personal", "rig"), "rig"),
        new("ds-id", Under("rtkelly-labs", "design-system"), "design-system"),
        new("work-id", Under("acme", "platform"), "platform"),
    ];

    public static string Home => Under("home");

    public static IReadOnlyList<string> Personal => [Under("personal"), Under("rtkelly-labs"), Under("Resultful")];

    public static IReadOnlyList<string> Work => [Under("acme"), Under("workspace")];

    public static string Under(params string[] parts) => Path.Combine([Root, .. parts]);

    public static double Ms(double minutesAgo) => (Now - TimeSpan.FromMinutes(minutesAgo)).ToUnixTimeMilliseconds();

    public static WrapupAgent Agent(
        string pane,
        string state = "done",
        double idle = 120,
        string prompt = "do the thing",
        string? reply = "Did the thing.",
        string agentType = "claude",
        string? parentPane = null,
        string? waitingMessage = null) => new(pane, state)
        {
            AgentType = agentType,
            ParentPaneKey = parentPane,
            Prompt = prompt,
            ConversationName = $"session {pane}",
            LastMessage = reply,
            StateStartedAt = Ms(idle),
            ProgramStatus = waitingMessage is null ? null : new("blocked", "question", waitingMessage),
        };

    public static WrapupWorktree Worktree(
        string id, string repo = "rig", string repoId = "rig-id", string? path = null, string? parent = null, WrapupPr? pr = null, params WrapupAgent[] agents) =>
        new(id, repoId, path ?? Under("personal", repo, "temp", id))
        {
            Repo = repo,
            DisplayName = id,
            Branch = $"refs/heads/rtkelly13/{id}",
            ParentId = parent,
            LinkedPr = pr,
            Agents = agents,
        };

    public static WrapupTerminal Terminal(string pane, string? identity = "claude", bool writable = true)
    {
        var parts = pane.Split(':');
        return new($"term_{parts[0]}", parts[0], parts[1], identity, writable);
    }

    public static WrapupSnapshot Snapshot(params WrapupWorktree[] worktrees) => new(worktrees, [], Repos);

    public static Task<IReadOnlyList<WrapupRow>> PlanAsync(
        IReadOnlyList<WrapupWorktree> worktrees,
        IReadOnlyList<WrapupTerminal>? terminals = null,
        GitFacts? facts = null,
        WrapupOptions? options = null,
        IWrapupStore? store = null) =>
        Wrapup.PlanAsync(
            [.. worktrees.Select(w => new WrapupPlaced(w, w.ParentId is null ? 0 : 1))],
            terminals ?? [],
            Now,
            options ?? new WrapupOptions(Wrapup.DefaultIdle, null),
            (_, _) => Task.FromResult<GitFacts?>(facts ?? Clean),
            store ?? new MemoryWrapupStore(),
            TestContext.Current.CancellationToken);

    /// <summary>One idle session per worktree <c>w0..</c>, each with its own live terminal.</summary>
    public static async Task<(IReadOnlyList<WrapupRow> Rows, Dictionary<string, WrapupWorktree> Worktrees)> SessionsAsync(int count)
    {
        WrapupWorktree[] worktrees = [.. Enumerable.Range(0, count).Select(n => Worktree($"w{n}", agents: Agent($"t{n}:l{n}")))];
        var rows = await PlanAsync(worktrees, [.. Enumerable.Range(0, count).Select(n => Terminal($"t{n}:l{n}"))]);
        return (rows, worktrees.ToDictionary(w => w.WorktreeId, StringComparer.Ordinal));
    }

    public static AskRecord Ask(double minutesAgo, string pane = "t1:l1", string id = "") =>
        new(WrapupStamps.Timestamp(Now - TimeSpan.FromMinutes(minutesAgo)), pane, "s", "abc", "clean", new string('0', 12), id);

    public static MemoryWrapupStore StoreWith(WrapupWorktree worktree, WrapupState state)
    {
        var store = new MemoryWrapupStore();
        store.Files[worktree.Path] = state;
        return store;
    }
}

/// <summary>State files in memory, keyed by worktree path; <see cref="Refuse"/> makes every write fail with that reason.</summary>
internal sealed class MemoryWrapupStore : IWrapupStore
{
    public Dictionary<string, WrapupState> Files { get; } = new(StringComparer.Ordinal);

    public string? Refuse { get; set; }

    /// <summary>Rewrites each state as it is written, as an agent reporting at that moment would.</summary>
    public Func<WrapupState, WrapupState>? OnWrite { get; set; }

    public WrapupState? Read(string worktree) => Files.GetValueOrDefault(worktree);

    public Task<string?> WriteAsync(string worktree, WrapupState state, CancellationToken cancellationToken = default)
    {
        if (Refuse is null)
        {
            Files[worktree] = OnWrite?.Invoke(state) ?? state;
        }

        return Task.FromResult(Refuse);
    }
}

/// <summary>Time that moves only when slept through, from <see cref="WrapupFixtures.Now"/>; <see cref="Interrupt"/> cancels the first sleep.</summary>
internal sealed class FakeWrapupClock : IWrapupClock
{
    public double Elapsed { get; private set; }

    public bool Interrupt { get; set; }

    public DateTimeOffset Now => WrapupFixtures.Now + TimeSpan.FromSeconds(Elapsed);

    public double Monotonic => Elapsed;

    public Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (Interrupt)
        {
            throw new OperationCanceledException();
        }

        Elapsed += Math.Max(delay.TotalSeconds, 0.001);
        return Task.CompletedTask;
    }
}
