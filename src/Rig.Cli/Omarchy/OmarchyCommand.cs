using System.CommandLine;
using System.Text.Json;
using DotMake.CommandLine;
using Rig.Cli.Commands;
using Rig.Cli.Rendering;
using Rig.Core;
using Rig.Core.Omarchy;
using Rig.Core.Packages;
using Rig.Core.Processes;
using Rig.Core.Text;

namespace Rig.Cli.Omarchy;

/// <summary>How <c>rig omarchy</c> reaches this machine's Omarchy install.</summary>
/// <param name="Paths">Where Omarchy keeps its files.</param>
/// <param name="PolicyFile">The rig checkout's <c>config/omarchy.json</c>.</param>
/// <param name="Error">Where a policy or read failure goes.</param>
internal sealed record OmarchyHost(OmarchyPaths Paths, string PolicyFile, TextWriter Error);

/// <summary>One row of <c>rig omarchy doctor --json</c>.</summary>
/// <param name="Area">The setting's area.</param>
/// <param name="Setting">Its name within the area.</param>
/// <param name="Have">What the machine has.</param>
/// <param name="Want">What the policy wants.</param>
/// <param name="Status">Whether they agree, differ, or could not be checked.</param>
/// <param name="Fix">The command or Settings change that makes them agree.</param>
internal sealed record OmarchyCheckRow(string Area, string Setting, string Have, string Want, string Status, string Fix);

/// <summary>One row of <c>rig omarchy sync --json</c>.</summary>
/// <param name="Area">The setting's area.</param>
/// <param name="Setting">Its name within the area.</param>
/// <param name="Have">What the machine had.</param>
/// <param name="Want">What the policy wants.</param>
/// <param name="Status">Planned, applied or error.</param>
/// <param name="Detail">What the apply did, or why it could not.</param>
internal sealed record OmarchyActionRow(string Area, string Setting, string Have, string Want, string Status, string Detail);

/// <summary>
/// <c>rig omarchy</c>: audit and sync this machine's Omarchy settings against <c>config/omarchy.json</c>.
/// </summary>
/// <param name="host">Where Omarchy and the policy are.</param>
/// <param name="proc">Runs <c>omarchy</c>, <c>systemctl</c> and <c>sudo</c>.</param>
/// <param name="output">Where reports go.</param>
[CliCommand(Name = "omarchy", Description = "Manage this machine's Omarchy desktop settings: idle, sleep, theme, power and shell.json.")]
internal sealed class OmarchyCommand(OmarchyHost host, IProc proc, TextWriter output)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal OmarchyCommand()
        : this(null!, null!, TextWriter.Null)
    {
    }

    /// <summary>The <c>omarchy</c> group.</summary>
    public Command Build()
    {
        var group = DeclarativeCommand.Build(this);
        group.Add(DeclarativeCommand.Build(new DoctorCommand(host, proc, output)));
        group.Add(DeclarativeCommand.Build(new SyncCommand(host, proc, output)));
        return group;
    }

    /// <summary>A group named without one of its verbs is the parser's error.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(host.Error);

    /// <summary><c>rig omarchy doctor</c>.</summary>
    [CliCommand(Name = "doctor", Description = "Compare this machine's Omarchy settings with config/omarchy.json. Read-only; exits 1 on any difference.")]
    internal sealed class DoctorCommand(OmarchyHost host, IProc proc, TextWriter output)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal DoctorCommand()
            : this(null!, null!, TextWriter.Null)
        {
        }

        /// <summary>Path to omarchy.json. Defaults to the checkout's config/omarchy.json.</summary>
        [CliOption(Name = "--config", Aliases = ["-c"], Required = false, Description = "Path to omarchy.json (default: the checkout's config/omarchy.json)")]
        public string? Config { get; set; }

        /// <summary>Output findings as JSON.</summary>
        [CliOption(Name = "--json", Description = "Output findings as JSON")]
        public bool Json { get; set; }

        /// <summary>Compares the machine and returns the exit code.</summary>
        public int Run()
        {
            try
            {
                var policy = OmarchyPolicy.Load(PolicyPath(host, Config));
                var state = OmarchyState.Read(host.Paths, proc);
                var checks = OmarchyPlan.Compare(policy, state);
                if (Json)
                {
                    output.WriteLine(Report(checks));
                }
                else
                {
                    var table = new TextTable("Omarchy vs config/omarchy.json", [("Area", false), ("Setting", false), ("Have", false), ("Want", false), ("Status", false)]);
                    foreach (var check in checks)
                    {
                        _ = table.Add(check.Area, check.Setting, check.Have, check.Want, check.Status.Label());
                    }

                    output.Write(table.Render());
                    output.WriteLine(Footer(checks));
                }

                return checks.Any(check => check.Status == CheckStatus.Fail) ? (int)ExitCode.Drift : (int)ExitCode.Clean;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                host.Error.WriteLine(e.Message);
                return (int)ExitCode.Error;
            }
        }
    }

    /// <summary><c>rig omarchy sync</c>.</summary>
    [CliCommand(Name = "sync", Description = "Plan the differences from config/omarchy.json, and with --apply write them. Exits 1 when a change failed.")]
    internal sealed class SyncCommand(OmarchyHost host, IProc proc, TextWriter output)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal SyncCommand()
            : this(null!, null!, TextWriter.Null)
        {
        }

        /// <summary>Write the planned changes. The logind drop-in asks sudo for a password.</summary>
        [CliOption(Name = "--apply", Description = "Write the planned changes (default is a dry run). The logind drop-in asks sudo for your password.")]
        public bool Apply { get; set; }

        /// <summary>Path to omarchy.json. Defaults to the checkout's config/omarchy.json.</summary>
        [CliOption(Name = "--config", Aliases = ["-c"], Required = false, Description = "Path to omarchy.json (default: the checkout's config/omarchy.json)")]
        public string? Config { get; set; }

        /// <summary>Output the plan as JSON.</summary>
        [CliOption(Name = "--json", Description = "Output the plan as JSON")]
        public bool Json { get; set; }

        /// <summary>Plans the differences and, with <see cref="Apply"/>, writes them.</summary>
        public async Task<int> RunAsync(CliContext context)
        {
            var cancellationToken = context.CancellationToken;
            try
            {
                var policy = OmarchyPolicy.Load(PolicyPath(host, Config));
                var state = OmarchyState.Read(host.Paths, proc);
                var actions = await OmarchySync.ApplyAsync(policy, state, host.Paths, proc, Apply, cancellationToken).ConfigureAwait(false);
                if (Json)
                {
                    await output.WriteLineAsync(Rows(actions)).ConfigureAwait(false);
                }
                else if (actions.Count == 0)
                {
                    await output.WriteLineAsync("This machine matches config/omarchy.json.").ConfigureAwait(false);
                }
                else
                {
                    var mode = Apply ? "APPLIED" : "DRY-RUN (pass --apply to execute)";
                    var table = new TextTable(
                        $"Omarchy Sync vs config/omarchy.json ({mode})",
                        [("Area", false), ("Setting", false), ("Have", false), ("Want", false), ("Status", false), ("Detail", false)]);
                    foreach (var action in actions)
                    {
                        _ = table.Add(action.Check.Area, action.Check.Setting, action.Check.Have, action.Check.Want, action.Status, action.Detail);
                    }

                    await output.WriteAsync(table.Render()).ConfigureAwait(false);
                }

                return actions.Any(action => action.Error) ? (int)ExitCode.Drift : (int)ExitCode.Clean;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                await host.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
                return (int)ExitCode.Error;
            }
        }
    }

    private static string PolicyPath(OmarchyHost host, string? config) =>
        string.IsNullOrEmpty(config) ? host.PolicyFile : Path.GetFullPath(config);

    private static string Footer(IReadOnlyList<OmarchyCheck> checks)
    {
        var failed = checks.Where(check => check.Status == CheckStatus.Fail).ToList();
        if (failed.Count == 0)
        {
            return "\n✓ This machine matches config/omarchy.json.\n";
        }

        return failed.Any(check => check.Area == "omarchy")
            ? $"\n{failed.First(check => check.Area == "omarchy").Fix}.\n"
            : "\nRun 'rig omarchy sync' to plan the changes or 'rig omarchy sync --apply' to write them.\n";
    }

    private static string Report(IReadOnlyList<OmarchyCheck> checks) =>
        JsonSerializer.Serialize(
            checks.Select(check => new OmarchyCheckRow(check.Area, check.Setting, check.Have, check.Want, check.Status.Label(), check.Fix)).ToList(),
            JsonOutputOptions.SnakeCaseIndented);

    private static string Rows(IReadOnlyList<OmarchyAction> actions) =>
        JsonSerializer.Serialize(
            actions.Select(action => new OmarchyActionRow(
                action.Check.Area,
                action.Check.Setting,
                action.Check.Have,
                action.Check.Want,
                action.Status,
                action.Detail)).ToList(),
            JsonOutputOptions.SnakeCaseIndented);
}
