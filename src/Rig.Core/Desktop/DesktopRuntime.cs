namespace Rig.Core.Desktop;

/// <summary>A window open on the machine now. Ported from <c>desktop_manager.models.LiveWindow</c>.</summary>
/// <param name="Handle">The window's handle.</param>
/// <param name="Title">Its title.</param>
/// <param name="Pid">The id of the process that owns it.</param>
/// <param name="ProcessName">That process's image name.</param>
/// <param name="ExePath">That process's executable.</param>
/// <param name="ClassName">The window class.</param>
/// <param name="DesktopNumber">The virtual desktop it is on, from 1, when known.</param>
/// <param name="ScreenNumber">The screen it is mostly on.</param>
/// <param name="InferredPlacement">The plan placement its bounds match, if any.</param>
/// <param name="Bounds">Its rectangle.</param>
public sealed record LiveWindow(
    long Handle,
    string Title,
    int? Pid = null,
    string? ProcessName = null,
    string? ExePath = null,
    string? ClassName = null,
    int? DesktopNumber = null,
    int? ScreenNumber = null,
    PlacementSpec? InferredPlacement = null,
    Rect? Bounds = null);

/// <summary>The window found for a launch entry, if any. Ported from <c>desktop_manager.models.WindowMatch</c>.</summary>
/// <param name="Handle">The window, or null when none showed up.</param>
/// <param name="MovedToTargetDesktop">Whether it had to be moved onto the target desktop.</param>
public sealed record WindowMatch(long? Handle, bool MovedToTargetDesktop = false);

/// <summary>Whether a launch entry's window was placed. Ported from <c>desktop_manager.models.PlacementOutcome</c>.</summary>
/// <param name="Placed">Whether the window was found and put in its slot.</param>
/// <param name="MovedToTargetDesktop">Whether it had to be moved onto the target desktop first.</param>
public sealed record PlacementOutcome(bool Placed, bool MovedToTargetDesktop = false);

/// <summary>What moving an existing window into an entry's place changed. Ported from <c>desktop_manager.window_reuse.WindowMove</c>.</summary>
/// <param name="MovedToDesktop">Whether it changed desktop.</param>
/// <param name="MovedToPlacement">Whether it changed position.</param>
public sealed record WindowMove(bool MovedToDesktop, bool MovedToPlacement);

/// <summary>How long to wait for an entry's window and where it should end up.</summary>
/// <param name="Timeout">How long to wait for it to show.</param>
/// <param name="TargetDesktop">The desktop it belongs on, from 1.</param>
/// <param name="Excluded">Windows that cannot be it, such as ones that existed before the launch.</param>
/// <param name="AllowProcessFallbackForTerminals">Whether a terminal may be found by its process when its title never shows.</param>
public sealed record WindowWait(
    TimeSpan Timeout,
    int TargetDesktop,
    IReadOnlySet<long>? Excluded = null,
    bool AllowProcessFallbackForTerminals = false);

/// <summary>Where restore writes what it did, as it does it. Replaces <c>RestoreRunLogger</c> and the diagnostic callbacks.</summary>
public interface IRunLog
{
    /// <summary>Record <paramref name="message"/>.</summary>
    void Log(string message);

    /// <summary>Record <paramref name="message"/> with the failure that prompted it, so the stack survives the run.</summary>
    void LogFailure(string message, Exception failure);
}

/// <summary>Run logs that need no state.</summary>
public static class RunLog
{
    /// <summary>A log that keeps nothing, for a run nobody asked to record.</summary>
    public static IRunLog Discard { get; } = new DiscardLog();

    private sealed class DiscardLog : IRunLog
    {
        public void Log(string message)
        {
        }

        public void LogFailure(string message, Exception failure)
        {
        }
    }
}

/// <summary>
/// An expected failure from the desktop: a window that vanished before it could be moved, or a launch that could not
/// start. Restore catches this per entry and carries on; anything else is a bug and stops the run.
/// </summary>
public sealed class DesktopException : Exception
{
    /// <summary>A failure with no message.</summary>
    public DesktopException()
    {
    }

    /// <summary>A failure described by <paramref name="message"/>.</summary>
    public DesktopException(string message)
        : base(message)
    {
    }

    /// <summary>A failure described by <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public DesktopException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
