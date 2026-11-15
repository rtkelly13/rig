using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// The input clock on macOS, from Quartz, for runs that must not move the mouse under the user. macOS exposes no
/// per-application device use, so a call is never reported; Spaces are not addressable, so the lock screen is never
/// treated as a reason to stop either. Ported from the macOS half of <c>desktop_manager.chrome.presence</c>.
/// </summary>
[SupportedOSPlatform("macos")]
[ExcludeFromCodeCoverage(Justification = "Quartz interop; no CI job runs macOS, and the readers around it are covered by their fakes.")]
public sealed partial class MacPresence : IPresence
{
    /// <summary>kCGEventSourceStateCombinedSessionState.</summary>
    private const uint CombinedSessionState = 0;

    /// <summary>kCGAnyInputEventType.</summary>
    private const uint AnyInputEventType = 0xFFFFFFFF;

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <inheritdoc/>
    public uint NowTick() => unchecked((uint)Environment.TickCount64);

    /// <inheritdoc/>
    public uint LastInputTick()
    {
        var idle = Math.Max(0, CGEventSourceSecondsSinceLastEventType(CombinedSessionState, AnyInputEventType));
        return unchecked(NowTick() - (uint)Math.Min(idle * 1000.0, uint.MaxValue));
    }

    /// <inheritdoc/>
    public bool SessionLocked() => false;

    /// <inheritdoc/>
    public IReadOnlyList<string> DevicesInUse() => [];

    [LibraryImport(CoreGraphics)]
    private static partial double CGEventSourceSecondsSinceLastEventType(uint state, uint eventType);
}
