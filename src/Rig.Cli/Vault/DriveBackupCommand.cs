using System.Globalization;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Core.Processes;
using Rig.Core.Vault;

namespace Rig.Cli.Vault;

/// <summary>
/// <c>rig vault drive-backup</c>: before a reconcile writes to Drive, pushes the Drive copy of every note that differs
/// from <c>origin/main</c> to a <c>backup/drive-diff-&lt;stamp&gt;</c> branch, so a conflict resolved in git's favour
/// stays recoverable (<see cref="DriveBackup"/>). Reports by default; <c>--apply</c> pushes. The Drive path is never
/// printed: its folder name is an account address.
/// </summary>
/// <param name="git">Runs git in the vault's repository.</param>
/// <param name="host">Where the two vault copies are.</param>
/// <param name="clock">Stamps the branch name.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "drive-backup", Description = "Push the Drive copy of every note that differs from main to a backup branch, before a reconcile.")]
internal sealed class DriveBackupCommand(Git git, VaultHost host, TimeProvider clock, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal DriveBackupCommand()
        : this(null!, null!, TimeProvider.System, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The prefix every backup branch shares.</summary>
    public const string BranchPrefix = "backup/drive-diff-";

    /// <summary>Commit and push the backup; without it, only report what differs.</summary>
    [CliOption(Name = "--apply", Description = "Commit and push the backup branch.")]
    public bool Apply { get; set; }

    /// <summary>Reports, or with <c>--apply</c> pushes, the backup.</summary>
    public async Task<int> RunAsync()
    {
        var drive = VaultPath.ResolveDrive(host.Environment, host.Home);
        if (!Directory.Exists(drive))
        {
            await error.WriteLineAsync($"No Drive copy of the vault found; set {VaultPath.DriveVariable}.").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var backup = new DriveBackup(git, VaultPath.Resolve(host.Environment, host.Home), drive);
        try
        {
            var plan = await backup.PlanAsync().ConfigureAwait(false);
            await output.WriteLineAsync($"{plan.Differ.Count} note(s) on Drive differ from main ({plan.Base[..12]})").ConfigureAwait(false);
            foreach (var note in plan.Differ)
            {
                await output.WriteLineAsync($"  {note}").ConfigureAwait(false);
            }

            if (plan.Differ.Count == 0)
            {
                await output.WriteLineAsync("Nothing to back up.").ConfigureAwait(false);
                return (int)ExitCode.Clean;
            }

            if (!Apply)
            {
                await output.WriteLineAsync("Dry run: pass --apply to push the backup branch.").ConfigureAwait(false);
                return (int)ExitCode.Drift;
            }

            var branch = BranchPrefix + clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var pushed = await backup.PushAsync(plan, branch, $"backup: Drive copies of {plan.Differ.Count} note(s) that differ from main").ConfigureAwait(false);
            await output.WriteLineAsync($"Pushed {pushed.Branch} ({pushed.Commit[..12]}). Never merge it; delete it once the reconcile is verified.").ConfigureAwait(false);
            return (int)ExitCode.Clean;
        }
        catch (DriveBackupException e)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
    }
}
