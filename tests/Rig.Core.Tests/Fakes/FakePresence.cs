using Rig.Core.Chrome;

namespace Rig.Core.Tests.Fakes;

/// <summary>A machine whose input clock, lock and device use the test sets.</summary>
internal sealed class FakePresence : IPresence
{
    public uint Now { get; set; } = 100_000;

    public uint LastInput { get; set; } = 100_000;

    public bool Locked { get; set; }

    public IReadOnlyList<string> Devices { get; set; } = [];

    /// <summary>When set, <see cref="DevicesInUse"/> throws it.</summary>
    public Exception? DeviceError { get; set; }

    /// <summary>Makes the machine idle for <paramref name="seconds"/>.</summary>
    public FakePresence Idle(double seconds)
    {
        LastInput = unchecked(Now - (uint)(seconds * 1000));
        return this;
    }

    public uint NowTick() => Now;

    public uint LastInputTick() => LastInput;

    public bool SessionLocked() => Locked;

    public IReadOnlyList<string> DevicesInUse() => DeviceError is null ? Devices : throw DeviceError;
}
