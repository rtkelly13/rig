namespace Rig.Core.Chrome.Extension;

/// <summary>
/// Presence for the extension driver: always away. The presence checks exist so the screen-driven drivers do not take
/// the keyboard and mouse from someone using them; the extension never touches either, so nothing needs to wait.
/// </summary>
public sealed class AwayPresence(TimeProvider time) : IPresence
{
    /// <inheritdoc/>
    public uint NowTick() => unchecked((uint)(time.GetTimestamp() * 1000 / time.TimestampFrequency));

    /// <summary>Long ago: the last input is as old as the tick count allows.</summary>
    public uint LastInputTick() => unchecked(NowTick() - (uint.MaxValue / 2));

    /// <inheritdoc/>
    public bool SessionLocked() => false;

    /// <inheritdoc/>
    public IReadOnlyList<string> DevicesInUse() => [];
}
