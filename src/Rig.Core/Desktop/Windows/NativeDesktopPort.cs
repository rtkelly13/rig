using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Rig.Core.Desktop.Windows;

/// <summary>
/// The real Windows machine as restore sees it: every read, write, wait and sleep through the Win32 layer and the
/// launcher.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Win32 interop; the Windows CI job smoke-tests it, the ubuntu coverage run cannot reach it.")]
public sealed class NativeDesktopPort : IDesktopPort
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly WindowInventory _inventory;
    private readonly WindowActions _windowActions;
    private readonly EntryWindows _entryWindows;
    private readonly IVirtualDesktops _desktops;
    private readonly Launching _launching;

    /// <summary>The machine over <paramref name="win32"/>, <paramref name="actions"/> and <paramref name="desktops"/>.</summary>
    public NativeDesktopPort(
        IWin32Windows win32,
        IWin32Actions actions,
        IVirtualDesktops desktops,
        Launching launching,
        TimeProvider? time = null)
    {
        _desktops = desktops;
        _launching = launching;
        _inventory = new WindowInventory(win32, desktops);
        _windowActions = new WindowActions(actions, _inventory);
        _entryWindows = new EntryWindows(_inventory, desktops, _windowActions, time);
    }

    /// <inheritdoc/>
    public TimeSpan Now => _clock.Elapsed;

    /// <inheritdoc/>
    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);

    /// <inheritdoc/>
    public WorkspacePlan CurrentWorkspaceStatus(int? desktopCount = null)
    {
        EnsureAvailable();
        var count = desktopCount ?? _desktops.Count;
        if (count < 1)
        {
            throw new DesktopException("Desktop count must be at least 1.");
        }

        return WorkspaceStatus.Current(_inventory, count);
    }

    /// <inheritdoc/>
    public WorkspacePlan CurrentWorkspaceStatus(IReadOnlyList<int> desktopIndexes)
    {
        EnsureAvailable();
        return WorkspaceStatus.CurrentForDesktopIndexes(_inventory, desktopIndexes);
    }

    /// <inheritdoc/>
    public DesktopPlan CurrentDesktopStatus(string? desktopName = null)
    {
        EnsureAvailable();
        return WorkspaceStatus.CurrentDesktop(_inventory, desktopName);
    }

    /// <inheritdoc/>
    public IReadOnlySet<long> WindowHandlesToPreserve() => _inventory.HandlesToPreserve();

    /// <inheritdoc/>
    public int? CurrentDesktopNumber() => _desktops.CurrentNumber is >= 1 ? _desktops.CurrentNumber : null;

    /// <inheritdoc/>
    public IReadOnlyList<LiveWindow> InventoryReusableWindows(IReadOnlySet<long> excluded) => _inventory.ReusableWindows(excluded);

    /// <inheritdoc/>
    public IReadOnlySet<long> WindowsMatchingProcess(string processName) => _inventory.WindowsMatchingProcess(processName, currentDesktopOnly: false).ToHashSet();

    /// <inheritdoc/>
    public IReadOnlyDictionary<long, string> WindowTitlesForProcess(string processName) =>
        _inventory.WindowTitlesForProcess(processName, currentDesktopOnly: false);

    /// <inheritdoc/>
    public bool IsProcessRunning(string processName) => _inventory.IsProcessRunning(processName);

    /// <inheritdoc/>
    public bool IsWindowTargetConfirmed(long handle, int desktopIndex, LaunchEntry entry)
    {
        if (_desktops.DesktopNumberOf(handle) is { } current && current != desktopIndex)
        {
            return false;
        }

        return entry.Placement is not { } placement
            || (_inventory.Bounds(handle) is { } bounds
                && Geometry.RectSimilarity(bounds, _windowActions.PlacementRectFor(placement)) >= 0.92);
    }

    /// <inheritdoc/>
    public bool IsEntryTargetConfirmed(LaunchEntry entry, int desktopIndex, TimeSpan timeout)
    {
        var match = _entryWindows.WaitForEntry(entry, timeout, currentDesktopOnly: false, targetDesktop: desktopIndex);
        return match.Handle is { } handle && IsWindowTargetConfirmed(handle, desktopIndex, entry);
    }

    /// <inheritdoc/>
    public WindowMatch WaitForWindowForEntry(LaunchEntry entry, WindowWait wait, IRunLog log)
    {
        return _entryWindows.WaitForEntry(
            entry,
            wait.Timeout,
            currentDesktopOnly: false,
            targetDesktop: wait.TargetDesktop,
            moveToTargetDesktop: false,
            excluded: wait.Excluded,
            allowProcessFallbackForTerminals: wait.AllowProcessFallbackForTerminals,
            diagnostic: log.Log);
    }

    /// <inheritdoc/>
    public string? WaitForWindowTitleChange(long handle, string title, TimeSpan timeout, IRunLog log)
    {
        return _entryWindows.WaitForTitleChange(handle, title, timeout, log.Log);
    }

    /// <inheritdoc/>
    public void EnsureAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new DesktopException("Workspace restore requires Windows.");
        }
    }

    /// <inheritdoc/>
    public bool SettleAppUpdates(WorkspacePlan plan, IRunLog log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (OrcaUpdate.Entries(plan).Count == 0)
        {
            log.Log("Orca update check skipped: the plan launches no Orca window");
            return false;
        }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var result = OrcaUpdate.Apply(plan, _inventory, _windowActions, localAppData, dryRun: false, log);
            return result.Ok && result.Actions.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            log.Log($"Orca update check failed: {ex.Message}; continuing with the restore");
            return false;
        }
    }

    /// <inheritdoc/>
    public void StartTerminalCommandUpgrades(WorkspacePlan plan)
    {
    }

    /// <inheritdoc/>
    public void EnsureDesktopCount(int count, TimeSpan settle)
    {
        _ = VirtualDesktopOperations.EnsureCount(_desktops, count);
        if (settle > TimeSpan.Zero)
        {
            Sleep(settle);
        }
    }

    /// <inheritdoc/>
    public bool EnsureCurrentDesktop(int desktopIndex, TimeSpan settle)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (VirtualDesktopOperations.GoTo(_desktops, desktopIndex))
            {
                if (settle > TimeSpan.Zero)
                {
                    Sleep(settle);
                }

                return true;
            }

            Sleep(TimeSpan.FromMilliseconds(200));
        }

        return false;
    }

    /// <inheritdoc/>
    public bool RenameDesktop(int desktopIndex, string name)
    {
        var renamed = VirtualDesktopOperations.Rename(_desktops, desktopIndex, name);
        Sleep(TimeSpan.FromMilliseconds(150));
        return renamed;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> RetireSwitchedOffDesktops(int keepCount, IReadOnlyList<string> retiredNames) =>
        VirtualDesktopOperations.RetireSwitchedOff(_desktops, keepCount, retiredNames);

    /// <inheritdoc/>
    public void MoveRight(int steps)
    {
        var target = Math.Min(_desktops.Count, Math.Max(1, _desktops.CurrentNumber + steps));
        _ = VirtualDesktopOperations.GoTo(_desktops, target);
    }

    /// <inheritdoc/>
    public void SwitchLeftmost(int steps)
    {
        _ = VirtualDesktopOperations.GoTo(_desktops, 1);
    }

    /// <inheritdoc/>
    public int CloseCurrentDesktopWindows(IReadOnlySet<long> preserved)
    {
        return _windowActions.CloseCurrentDesktopWindows(preserved);
    }

    /// <inheritdoc/>
    public WindowMove MoveExistingWindowToEntry(LiveWindow window, int desktopIndex, LaunchEntry entry)
    {
        var movedToDesktop = false;
        if (_desktops.DesktopNumberOf(window.Handle) != desktopIndex)
        {
            movedToDesktop = VirtualDesktopOperations.MoveWindow(_desktops, window.Handle, desktopIndex);
        }

        var movedToPlacement = false;
        if (entry.Placement is { } placement && !IsWindowTargetConfirmed(window.Handle, desktopIndex, entry))
        {
            _windowActions.Place(window.Handle, _windowActions.PlacementRectFor(placement));
            movedToPlacement = true;
        }

        return new(movedToDesktop, movedToPlacement);
    }

    /// <inheritdoc/>
    public int? Launch(LaunchEntry entry, IReadOnlyDictionary<string, string> substitutions, string? restoreToken = null)
    {
        return _launching.Launch(entry, substitutions, restoreToken);
    }

    /// <inheritdoc/>
    public PlacementOutcome ApplyWindowPlacement(LaunchEntry entry, WindowWait wait, IRunLog log, long? handle = null)
    {
        return entry.Placement is null
            ? new(false)
            : _entryWindows.ApplyPlacement(entry, wait.Timeout, wait.TargetDesktop, wait.Excluded, handle, log.Log);
    }

    /// <inheritdoc/>
    public int BringProcessWindowsToFront(IReadOnlySet<string> processNames)
    {
        var raised = 0;
        foreach (var name in processNames)
        {
            foreach (var handle in _inventory.WindowsMatchingProcess(name, currentDesktopOnly: false))
            {
                _windowActions.BringToFront(handle);
                raised++;
            }
        }

        return raised;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> OpenMissingPathFallbacks(RestoreFailureFallbacks fallbacks, IRunLog log)
    {
        return [];
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ApplySecondPassPlacements(
        WorkspacePlan plan,
        IReadOnlyDictionary<int, IReadOnlyList<LaunchEntry>> entriesByDesktop,
        IRunLog log)
    {
        var actions = new List<string>();
        SwitchLeftmost(plan.Desktops.Count + 2);
        Sleep(TimeSpan.FromMilliseconds(plan.SwitchDelayMs));
        _ = EnsureCurrentDesktop(1, TimeSpan.FromMilliseconds(plan.SwitchDelayMs));

        for (var desktopIndex = 1; desktopIndex <= plan.Desktops.Count; desktopIndex++)
        {
            if (desktopIndex > 1)
            {
                MoveRight(1);
                Sleep(TimeSpan.FromMilliseconds(plan.SwitchDelayMs));
            }

            _ = EnsureCurrentDesktop(desktopIndex, TimeSpan.FromMilliseconds(plan.SwitchDelayMs));
            if (!entriesByDesktop.TryGetValue(desktopIndex, out var entries) || entries.Count == 0)
            {
                continue;
            }

            actions.Add($"Desktop {desktopIndex}: second-pass placement");
            foreach (var entry in entries)
            {
                if (entry.Disabled || entry.Placement is null)
                {
                    continue;
                }

                if (entry.Type == LaunchEntry.Terminal)
                {
                    log.Log($"Skipped second-pass placement for terminal {entry.Name}");
                    continue;
                }

                try
                {
                    long? existing = null;
                    if (!string.IsNullOrEmpty(entry.WindowTitle))
                    {
                        existing = _entryWindows.WaitByTitle(entry.WindowTitle, TimeSpan.FromMilliseconds(200), currentDesktopOnly: false, diagnostic: log.Log);
                    }

                    if (existing is null && !string.IsNullOrEmpty(entry.WindowProcessName))
                    {
                        existing = _entryWindows.WaitByProcess(
                            entry.WindowProcessName,
                            TimeSpan.FromMilliseconds(200),
                            currentDesktopOnly: false,
                            targetDesktop: desktopIndex,
                            diagnostic: log.Log);
                    }

                    if (existing is { } handle && IsWindowTargetConfirmed(handle, desktopIndex, entry))
                    {
                        continue;
                    }

                    var outcome = ApplyWindowPlacement(entry, new(TimeSpan.FromSeconds(1), desktopIndex), log);
                    if (outcome.MovedToTargetDesktop)
                    {
                        log.Log($"Moved launched window for {entry.Name} onto desktop {desktopIndex} during second pass");
                    }

                    if (outcome.Placed)
                    {
                        actions.Add($"  - reposition `{entry.Name}`");
                        log.Log($"Second-pass placement applied to {entry.Name}");
                    }
                }
                catch (DesktopException error)
                {
                    log.LogFailure($"FAILED second-pass placement {entry.Name}: {error.Message}", error);
                }
            }
        }

        SwitchLeftmost(plan.Desktops.Count + 2);
        Sleep(TimeSpan.FromMilliseconds(plan.SwitchDelayMs));
        _ = EnsureCurrentDesktop(1, TimeSpan.FromMilliseconds(plan.SwitchDelayMs));
        return actions;
    }

    /// <inheritdoc/>
    public Rect PlacementRect(PlacementSpec placement)
    {
        return _windowActions.PlacementRectFor(placement);
    }
}
