using Rig.Core.Chrome;

namespace Rig.Core.Tests.Fakes;

/// <summary>Records every grouping step; each succeeds unless the test says otherwise.</summary>
internal sealed class FakeChromeGroupActions : IChromeGroupActions
{
    /// <summary>Every call, in order: <c>front 1000</c>, <c>back 1000</c>, <c>ungroup Dup</c>, <c>move 7 to 2000</c>, <c>group 7 rig new</c>.</summary>
    public List<string> Log { get; } = [];

    /// <summary>The tabs grouped, as the driver was handed them.</summary>
    public List<ChromeTab> Grouped { get; } = [];

    /// <summary>Windows that will not come to the front.</summary>
    public HashSet<long> Refuses { get; } = [];

    /// <summary>The detail of the first group step to fail, at <see cref="FailGroupAt"/>.</summary>
    public string? GroupFailure { get; set; }

    /// <summary>The 0-based group step that fails with <see cref="GroupFailure"/>.</summary>
    public int FailGroupAt { get; set; }

    /// <summary>When set, ungrouping fails with this detail.</summary>
    public string? UngroupFailure { get; set; }

    /// <summary>When set, moving a tab to another window fails with this detail.</summary>
    public string? MoveFailure { get; set; }

    /// <summary>Runs inside every group step, before it reports.</summary>
    public Action? DuringGroup { get; set; }

    public ForegroundLease Foreground(long handle, TimeSpan? timeout = null)
    {
        Log.Add($"front {handle}");
        return Refuses.Contains(handle) ? new ForegroundLease(false) : new ForegroundLease(true, () => Log.Add($"back {handle}"));
    }

    public GroupOutcome GroupTab(ChromeTab tab, string title, bool isNew, long? homeHandle = null, TimeSpan? timeout = null)
    {
        Log.Add($"group {tab.Title} {title}{(isNew ? " new" : string.Empty)}");
        DuringGroup?.Invoke();
        var failed = GroupFailure is not null && Grouped.Count == FailGroupAt;
        Grouped.Add(tab);
        return new GroupOutcome(!failed, failed ? GroupFailure! : "grouped", TimeSpan.Zero);
    }

    public GroupOutcome Ungroup(ChromeTab tab, string title, TimeSpan? timeout = null)
    {
        Log.Add($"ungroup {title}");
        return new GroupOutcome(UngroupFailure is null, UngroupFailure ?? "ungrouped", TimeSpan.Zero);
    }

    public MoveOutcome MoveToWindow(ChromeTab tab, long targetHandle, TimeSpan? timeout = null)
    {
        Log.Add($"move {tab.Title} to {targetHandle}");
        var outcome = new GroupOutcome(MoveFailure is null, MoveFailure ?? "moved", TimeSpan.Zero);
        return MoveFailure is null
            ? new MoveOutcome(outcome, tab with { RuntimeId = new RuntimeId([.. tab.RuntimeId.Parts, 0]), WindowHandle = targetHandle })
            : new MoveOutcome(outcome);
    }
}

/// <summary>Numbered virtual desktops with windows on them; moves land at once unless a window is stuck.</summary>
internal sealed class FakeChromeDesktops : IChromeDesktops
{
    /// <summary>The desktop the user is on; null for unaddressable desktops.</summary>
    public int? Number { get; set; } = 1;

    /// <summary>Each window's desktop, by handle.</summary>
    public Dictionary<long, int> Windows { get; } = [];

    /// <summary>Every switch the runner made.</summary>
    public List<int> Went { get; } = [];

    /// <summary>Every move made, as (window, desktop).</summary>
    public List<(long Handle, int Desktop)> Moves { get; } = [];

    /// <summary>Windows whose moves never land.</summary>
    public HashSet<long> Stuck { get; } = [];

    /// <summary>Windows that throw when moved.</summary>
    public HashSet<long> Unmovable { get; } = [];

    /// <summary>Windows closed while the run worked.</summary>
    public HashSet<long> Gone { get; } = [];

    /// <summary>When true, <see cref="Current"/> throws.</summary>
    public bool Broken { get; set; }

    /// <summary>When true, <see cref="DesktopOf"/> throws.</summary>
    public bool UnreadableWindows { get; set; }

    public int? Current() => Broken ? throw new InvalidOperationException("desktops") : Number;

    public int? DesktopOf(long handle) =>
        UnreadableWindows ? throw new InvalidOperationException("desktops") : Windows.TryGetValue(handle, out var number) ? number : null;

    public void SwitchTo(int number)
    {
        Went.Add(number);
        Number = number;
    }

    public void MoveWindow(long handle, int number)
    {
        if (Unmovable.Contains(handle))
        {
            throw new InvalidOperationException("will not move");
        }

        Moves.Add((handle, number));
        if (!Stuck.Contains(handle))
        {
            Windows[handle] = number;
        }
    }

    public bool WindowExists(long handle) => !Gone.Contains(handle);
}
