using Rig.Core.Processes;

namespace Rig.Core.Jobs;

/// <summary>The stable paths registered in Task Scheduler, through current for a runtime copy.</summary>
/// <param name="Root">The checkout or runtime/current directory.</param>
/// <param name="Plan">The guard's workspace plan.</param>
/// <param name="Rigw">The published windowless coordinator.</param>
public sealed record JobsInstallTarget(string Root, string Plan, string Rigw)
{
    /// <summary>Resolves the install paths, refuses linked worktrees and validates every target before changing tasks.</summary>
    public static async Task<JobsInstallTarget> ResolveAsync(string checkout, string appData, string userPlan, IProc proc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proc);
        checkout = Path.GetFullPath(checkout);
        JobsInstallTarget target;
        if (JobsSite.CopyOf(Path.Join(checkout, JobsSite.PublishFolder), appData) is not null)
        {
            var root = Path.Join(appData, JobsSite.RuntimeFolder, "current");
            target = new(root, Path.Join(root, "desktop-manager", "workspace-plan.bundle.json"), Path.Join(root, JobsSite.PublishFolder, "rigw.exe"));
        }
        else
        {
            var result = await proc.RunAsync(new ProcRequest(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"]) { WorkingDirectory = checkout }, cancellationToken).ConfigureAwait(false);
            if (!result.Ok && (File.Exists(Path.Join(checkout, ".git")) || Directory.Exists(Path.Join(checkout, ".git"))))
            {
                throw new InvalidOperationException("Cannot verify the main checkout; git rev-parse failed. No tasks were changed.");
            }
            if (result.Ok && result.OutOrEmpty.Length > 0 && Path.GetDirectoryName(result.OutOrEmpty) is { } main
                && Path.GetRelativePath(checkout, main) != ".")
            {
                throw new InvalidOperationException($"jobs install must run from the main checkout or a runtime copy, not a worktree.\nMain checkout: {main}");
            }

            target = new(checkout, userPlan, Path.Join(checkout, "temp", "jobs-runtime", "rigw.exe"));
        }

        foreach (var path in new[] { target.Rigw, target.Plan })
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"jobs install target is missing: {path}. Install a runtime copy, or publish src/Rig.Launcher to temp/jobs-runtime in the main checkout.");
            }
        }

        return target;
    }
}
