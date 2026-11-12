namespace Rig.Core.Chrome;

/// <summary>Why the caller may not go ahead yet.</summary>
/// <param name="Reason">Why, for people.</param>
/// <param name="ClearsIn">Seconds until it might clear; null when unknown.</param>
public sealed record Block(string Reason, double? ClearsIn = null);

/// <summary>How a <see cref="Presence.WaitUntilClear"/> went.</summary>
/// <param name="Ready">The block cleared within the budget.</param>
/// <param name="WaitedSeconds">How long the wait took.</param>
/// <param name="Checks">How many times it asked.</param>
/// <param name="LastBlock">The last block's reason, if any was seen.</param>
public sealed record WaitOutcome(bool Ready, double WaitedSeconds, int Checks, string? LastBlock);

/// <summary>
/// Whether the user is at the keyboard or on a call, as the machine reports it. Windows records the tick of the last
/// keyboard or mouse input from any source, the grouping's own clicks included.
/// </summary>
public interface IPresence
{
    /// <summary>The millisecond tick count now, wrapping at 2^32 like <c>GetTickCount</c>.</summary>
    uint NowTick();

    /// <summary>The tick of the last keyboard or mouse input.</summary>
    uint LastInputTick();

    /// <summary>The lock screen holds the input desktop, so nothing can take the foreground or read the cursor.</summary>
    bool SessionLocked();

    /// <summary>The devices (<c>microphone</c>, <c>camera</c>) Chrome is using now, muted calls included.</summary>
    IReadOnlyList<string> DevicesInUse();
}

/// <summary>Idle time and waiting for a block to clear. Ported from <c>desktop_manager.chrome.presence</c>.</summary>
public static class Presence
{
    /// <summary>Seconds since the last input, across a tick-count wrap.</summary>
    public static double IdleSeconds(IPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        return unchecked(presence.NowTick() - presence.LastInputTick()) / 1000.0;
    }

    /// <summary>
    /// Asks <paramref name="blocked"/> until it clears or <paramref name="budgetSeconds"/> runs out, sleeping in between.
    /// Each pause doubles up to <paramref name="maxPause"/>, so a busy machine costs a handful of cheap checks an hour.
    /// A block that says when it may clear is checked again exactly then rather than after the full pause.
    /// </summary>
    public static WaitOutcome WaitUntilClear(
        Func<Block?> blocked,
        double budgetSeconds,
        Action<double> sleep,
        Func<double> clock,
        double firstPause = 5.0,
        double maxPause = 60.0)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        ArgumentNullException.ThrowIfNull(sleep);
        ArgumentNullException.ThrowIfNull(clock);
        var started = clock();
        var pause = firstPause;
        var checks = 0;
        string? last = null;
        while (true)
        {
            checks++;
            if (blocked() is not { } block)
            {
                return new WaitOutcome(true, clock() - started, checks, last);
            }

            last = block.Reason;
            var remaining = budgetSeconds - (clock() - started);
            if (remaining <= 0)
            {
                return new WaitOutcome(false, clock() - started, checks, last);
            }

            var step = block.ClearsIn is { } hint ? Math.Min(pause, Math.Max(hint, 0.5)) : pause;
            sleep(Math.Min(step, remaining));
            pause = Math.Min(pause * 2, maxPause);
        }
    }
}

/// <summary>
/// Notices input that arrives after the caller's own last step: synthetic input counts as input too, so the caller
/// marks the tick after each step of its own and anything later is the user's.
/// </summary>
public sealed class InputWatch
{
    /// <summary>Our own events land a moment after the call that sends them returns.</summary>
    private const uint GraceMs = 300;

    private readonly IPresence _presence;
    private uint _mark;

    /// <summary>Starts watching from now.</summary>
    public InputWatch(IPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        _presence = presence;
        _mark = presence.NowTick();
    }

    /// <summary>Call after each step that sends input of its own.</summary>
    public void Mark() => _mark = _presence.NowTick();

    /// <summary>Input arrived after the last mark, beyond the grace for our own.</summary>
    public bool UserMoved()
    {
        var sinceMark = unchecked(_presence.LastInputTick() - _mark);
        return sinceMark is > GraceMs and < (1u << 31);
    }
}
