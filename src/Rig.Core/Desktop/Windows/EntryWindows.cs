using System.Globalization;
using Rig.Core.Vault;

namespace Rig.Core.Desktop.Windows;

/// <summary>
/// Finding an entry's window on the machine and putting it in place. Ported from
/// <c>desktop_manager.win32.entry_windows</c>.
/// </summary>
/// <param name="inventory">The window reads.</param>
/// <param name="desktops">Which virtual desktop a window is on, and how to move it.</param>
/// <param name="actions">The window writes.</param>
/// <param name="time">The clock the waits poll against.</param>
public sealed class EntryWindows(WindowInventory inventory, IVirtualDesktops desktops, WindowActions actions, TimeProvider? time = null)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// The best of <paramref name="handles"/> for <paramref name="placement"/> on <paramref name="targetDesktop"/>:
    /// the one already on the target desktop counts most, then the one closest to the slot.
    /// </summary>
    public long ChooseBestWindow(IReadOnlyList<long> handles, PlacementSpec? placement, int? targetDesktop = null)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (handles.Count == 0)
        {
            throw new ArgumentException("Expected at least one window handle.", nameof(handles));
        }

        if (placement is null && targetDesktop is null)
        {
            return handles[0];
        }

        var best = handles[0];
        var bestScore = -1.0;
        var targetBounds = placement is null ? null : actions.PlacementRectFor(placement);
        foreach (var handle in handles)
        {
            var score = 0.0;
            if (targetDesktop is { } desktop && desktops.DesktopNumberOf(handle) == desktop)
            {
                score += 2.0;
            }

            if (targetBounds is { } bounds)
            {
                if (inventory.Bounds(handle) is not { } windowBounds)
                {
                    continue;
                }

                score += Geometry.RectSimilarity(windowBounds, bounds);
            }

            if (score > bestScore)
            {
                (best, bestScore) = (handle, score);
            }
        }

        return best;
    }

    /// <summary>The first window whose title contains <paramref name="title"/>, waiting up to <paramref name="timeout"/>.</summary>
    public long? WaitByTitle(
        string title,
        TimeSpan timeout,
        bool exact = false,
        bool currentDesktopOnly = true,
        IReadOnlySet<long>? excluded = null,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        var deadline = _time.GetUtcNow() + timeout;
        var target = PyText.CaseFold(title);
        excluded ??= new HashSet<long>();
        diagnostic?.Invoke(
            $"Polling title target={title} exact={exact} current_desktop_only={currentDesktopOnly} "
            + $"excluded={string.Join(",", excluded)} timeout={timeout.TotalSeconds:F2}s");

        while (_time.GetUtcNow() < deadline)
        {
            var matches = inventory.MatchingWindows(target, exact, currentDesktopOnly);
            var filtered = matches.Where(handle => !excluded.Contains(handle)).ToList();
            if (filtered.Count > 0)
            {
                diagnostic?.Invoke($"Matched title target={title} hwnds={string.Join(",", matches)} filtered={string.Join(",", filtered)}");
                return filtered[0];
            }

            if (matches.Count > 0)
            {
                diagnostic?.Invoke($"Title target={title} only matched excluded hwnds={string.Join(",", matches)}; waiting for fresh window");
            }

            Thread.Sleep(Poll);
        }

        diagnostic?.Invoke($"Timed out waiting for title target={title}");
        return null;
    }

    /// <summary>A window of the named process, waiting up to <paramref name="timeout"/> and once more while it is alive.</summary>
    public long? WaitByProcess(
        string processName,
        TimeSpan timeout,
        PlacementSpec? targetPlacement = null,
        bool currentDesktopOnly = true,
        int? targetDesktop = null,
        IReadOnlySet<long>? excluded = null,
        bool singleInstance = false,
        TimeSpan? aliveProcessGrace = null,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(processName);
        var target = PyText.CaseFold(processName);
        var deadline = _time.GetUtcNow() + timeout;
        var grace = aliveProcessGrace ?? TimeSpan.Zero;
        diagnostic?.Invoke(
            $"Polling process target={processName} current_desktop_only={currentDesktopOnly} target_desktop={targetDesktop?.ToString(CultureInfo.InvariantCulture) ?? "none"} "
            + $"excluded={(excluded is null ? string.Empty : string.Join(",", excluded))} timeout={timeout.TotalSeconds:F2}s grace={grace.TotalSeconds:F2}s");

        var extended = false;
        while (true)
        {
            while (_time.GetUtcNow() < deadline)
            {
                var matches = inventory.WindowsMatchingProcess(target, currentDesktopOnly);
                if (matches.Count > 0)
                {
                    var original = matches.ToList();
                    if (excluded is { Count: > 0 })
                    {
                        var fresh = matches.Where(handle => !excluded.Contains(handle)).ToList();
                        if (fresh.Count == 0)
                        {
                            if (singleInstance)
                            {
                                var chosen = ChooseBestWindow(original, targetPlacement, targetDesktop);
                                diagnostic?.Invoke(
                                    $"Process target={processName} is single-instance; reusing existing window {chosen} "
                                    + $"from hwnds={string.Join(",", original)}");
                                return chosen;
                            }

                            diagnostic?.Invoke(
                                $"Process target={processName} only matched excluded hwnds={string.Join(",", original)}; "
                                + "waiting for fresh window");
                            Thread.Sleep(Poll);
                            continue;
                        }

                        matches = fresh;
                    }

                    var best = ChooseBestWindow(matches, targetPlacement, targetDesktop);
                    diagnostic?.Invoke(
                        $"Matched process target={processName} hwnds={string.Join(",", original)} "
                        + $"filtered={string.Join(",", matches)} chosen={best}");
                    return best;
                }

                Thread.Sleep(Poll);
            }

            // At logon the process often starts within a second but takes far longer to paint its first window while
            // the disk is saturated. Extend the wait once while the process is demonstrably alive.
            if (extended || grace <= TimeSpan.Zero || !inventory.IsProcessRunning(target))
            {
                break;
            }

            extended = true;
            deadline = _time.GetUtcNow() + grace;
            diagnostic?.Invoke($"Process target={processName} is running but has no window yet; extending wait by {grace.TotalSeconds:F2}s");
        }

        diagnostic?.Invoke($"Timed out waiting for process target={processName}");
        return null;
    }

    /// <summary>Wait for <paramref name="handle"/> to take a title other than <paramref name="title"/>; its title then, or null when it is gone.</summary>
    public string? WaitForTitleChange(long handle, string title, TimeSpan timeout, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        var deadline = _time.GetUtcNow() + timeout;
        var target = PyText.CaseFold(title);
        diagnostic?.Invoke($"Waiting for hwnd={handle} title to change from {title} within {timeout.TotalSeconds:F2}s");
        while (_time.GetUtcNow() < deadline)
        {
            var current = inventory.Title(handle);
            if (!string.IsNullOrEmpty(current) && PyText.CaseFold(current) != target)
            {
                diagnostic?.Invoke($"Window hwnd={handle} changed title to {current}");
                return current;
            }

            Thread.Sleep(Poll);
        }

        diagnostic?.Invoke($"Timed out waiting for hwnd={handle} title to change from {title}");
        return null;
    }

    /// <summary>Move a window onto <paramref name="targetDesktop"/> when it is elsewhere; whether it moved.</summary>
    public bool MoveToTargetDesktop(long handle, int? targetDesktop)
    {
        if (targetDesktop is not { } desktop || desktops.DesktopNumberOf(handle) == desktop)
        {
            return false;
        }

        if (!VirtualDesktopOperations.MoveWindow(desktops, handle, desktop))
        {
            return false;
        }

        Thread.Sleep(Poll);
        return true;
    }

    /// <summary>
    /// Find <paramref name="entry"/>'s window and put it in its slot. <paramref name="handle"/> skips the search.
    /// </summary>
    /// <exception cref="DesktopException">No window could be found for the entry.</exception>
    public PlacementOutcome ApplyPlacement(
        LaunchEntry entry,
        TimeSpan timeout,
        int? targetDesktop = null,
        IReadOnlySet<long>? excluded = null,
        long? handle = null,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Placement is not { } placement)
        {
            return new(false);
        }

        diagnostic?.Invoke(
            $"Applying placement for {entry.Name} target_desktop={targetDesktop?.ToString(CultureInfo.InvariantCulture) ?? "none"} placement={placement.ToJson().ToJsonString()}");

        var movedToTargetDesktop = false;
        if (handle is null)
        {
            var match = WaitForEntry(
                entry,
                timeout,
                currentDesktopOnly: targetDesktop is null,
                targetDesktop: targetDesktop,
                moveToTargetDesktop: targetDesktop is not null,
                excluded: excluded,
                diagnostic: diagnostic);
            handle = match.Handle;
            movedToTargetDesktop = match.MovedToTargetDesktop;
        }
        else if (targetDesktop is not null)
        {
            movedToTargetDesktop = MoveToTargetDesktop(handle.Value, targetDesktop);
            if (movedToTargetDesktop)
            {
                diagnostic?.Invoke($"Moved hwnd={handle} for {entry.Name} to desktop {targetDesktop}");
            }
        }

        if (handle is not { } found)
        {
            throw new DesktopException($"Could not find window for placement: {entry.Name}");
        }

        var rect = actions.PlacementRectFor(placement);
        diagnostic?.Invoke($"Moving hwnd={found} for {entry.Name} to rect={rect}");
        actions.Move(found, rect);
        return new(true, movedToTargetDesktop);
    }

    /// <summary>Wait for <paramref name="entry"/>'s window and, when asked, bring it to the target desktop.</summary>
    public WindowMatch WaitForEntry(
        LaunchEntry entry,
        TimeSpan timeout,
        bool currentDesktopOnly = true,
        int? targetDesktop = null,
        bool moveToTargetDesktop = false,
        IReadOnlySet<long>? excluded = null,
        bool allowProcessFallbackForTerminals = false,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        diagnostic?.Invoke(
            $"Waiting for entry {entry.Name} title={entry.WindowTitle ?? "none"} process={entry.WindowProcessName ?? "none"} "
            + $"current_desktop_only={currentDesktopOnly} target_desktop={targetDesktop?.ToString(CultureInfo.InvariantCulture) ?? "none"} "
            + $"move_to_target_desktop={moveToTargetDesktop} allow_terminal_process_fallback={allowProcessFallbackForTerminals}");

        if (!string.IsNullOrEmpty(entry.WindowTitle))
        {
            var byTitle = WaitByTitle(entry.WindowTitle, timeout, exact: false, currentDesktopOnly, excluded, diagnostic);
            if (byTitle is { } handle)
            {
                return Matched(handle, moveToTargetDesktop, targetDesktop, diagnostic);
            }

            if (entry.Type == LaunchEntry.Terminal && !allowProcessFallbackForTerminals)
            {
                diagnostic?.Invoke($"Terminal {entry.Name} stopped after title polling without process fallback");
                return new(null);
            }

            if (entry.Type == LaunchEntry.Terminal)
            {
                diagnostic?.Invoke($"Terminal {entry.Name} title changed or was unavailable; falling back to process polling");
            }
        }

        if (!string.IsNullOrEmpty(entry.WindowProcessName))
        {
            var byProcess = WaitByProcess(
                entry.WindowProcessName,
                timeout,
                entry.Placement,
                currentDesktopOnly,
                targetDesktop,
                excluded,
                entry.SingleInstance,
                StepTimer.EntryWindowWaitGrace(entry),
                diagnostic);
            if (byProcess is { } handle)
            {
                return Matched(handle, moveToTargetDesktop, targetDesktop, diagnostic);
            }
        }

        return new(null);
    }

    private WindowMatch Matched(long handle, bool moveToTargetDesktop, int? targetDesktop, Action<string>? diagnostic)
    {
        var moved = moveToTargetDesktop && MoveToTargetDesktop(handle, targetDesktop);
        if (moved)
        {
            diagnostic?.Invoke($"Moved hwnd={handle} to desktop {targetDesktop}");
        }

        return new(handle, moved);
    }
}
