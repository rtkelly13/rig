namespace Rig.Core.Desktop;

/// <summary>
/// Putting away the windows of the plan's <see cref="WorkspacePlan.Background"/> apps: the ones that start at sign-in,
/// belong in the tray, and open a window anyway, such as Docker Desktop's dashboard.
/// </summary>
public static class BackgroundApps
{
    /// <summary>How often restore looks again for the window of an app it is waiting on.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    /// <summary>What a dry run would do for each enabled app.</summary>
    public static IReadOnlyList<string> Planned(WorkspacePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. Enabled(plan).Select(app => $"{Verb(app)} any window of background app {app.Name} ({app.ProcessName})")];
    }

    /// <summary>
    /// Close or minimize every open window of each enabled app, skipping the invoking terminal's. An app
    /// that is running with no window yet is waited on for its <see cref="BackgroundApp.WindowWait"/>; one that is not
    /// running is left alone. The actions taken.
    /// </summary>
    public static IReadOnlyList<string> PutAway(RestoreRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (port, log) = (run.Port, run.Log);
        var actions = new List<string>();
        var pending = Enabled(run.Plan).ToList();
        var started = port.Now;
        var preserved = run.Preserved.Count > 0 ? run.Preserved : port.WindowHandlesToPreserve();
        while (pending.Count > 0)
        {
            foreach (var app in pending.ToList())
            {
                var handles = port.WindowsMatchingProcess(app.ProcessName).Where(handle => !preserved.Contains(handle)).Order().ToList();
                if (handles.Count > 0)
                {
                    foreach (var handle in handles)
                    {
                        port.PutAwayWindow(handle, app.Action);
                    }

                    var action = $"{Verb(app)} {handles.Count} window(s) of background app {app.Name} ({app.ProcessName})";
                    actions.Add(action);
                    log.Log(action);
                    _ = pending.Remove(app);
                }
                else if (!port.IsProcessRunning(app.ProcessName))
                {
                    log.Log($"Background app {app.Name} is not running; nothing to put away");
                    _ = pending.Remove(app);
                }
                else if (port.Now - started >= app.WindowWait)
                {
                    log.Log($"Background app {app.Name} has no window open; nothing to put away");
                    _ = pending.Remove(app);
                }
            }

            if (pending.Count > 0)
            {
                port.Sleep(Poll);
            }
        }

        return actions;
    }

    private static IEnumerable<BackgroundApp> Enabled(WorkspacePlan plan) => plan.Background.Where(app => !app.Disabled);

    private static string Verb(BackgroundApp app) => app.Action == BackgroundApp.Minimize ? "Minimize" : "Close";
}
