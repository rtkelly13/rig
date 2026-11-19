#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Text.Json.Nodes;
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

/// <summary>
/// <c>rig omarchy</c>: audit and sync this machine's Omarchy settings against <c>config/omarchy.json</c>.
/// </summary>
/// <param name="host">Where Omarchy and the policy are.</param>
/// <param name="proc">Runs <c>omarchy</c>, <c>systemctl</c> and <c>sudo</c>.</param>
/// <param name="output">Where reports go.</param>
internal sealed class OmarchyCommand(OmarchyHost host, IProc proc, TextWriter output)
{
    /// <summary>The <c>omarchy</c> group.</summary>
    public Command Build()
    {
        var group = new Command("omarchy", "Manage this machine's Omarchy desktop settings: idle, sleep, theme, power and shell.json.");
        group.Subcommands.Add(Doctor());
        group.Subcommands.Add(Sync());
        return group;
    }

    private static Option<string?> ConfigOption() => new("--config", "-c") { Description = "Path to omarchy.json (default: the checkout's config/omarchy.json)" };

    private Command Doctor()
    {
        var config = ConfigOption();
        var json = new Option<bool>("--json") { Description = "Output findings as JSON" };
        var doctor = new Command("doctor", "Compare this machine's Omarchy settings with config/omarchy.json. Read-only; exits 1 on any difference.") { config, json };
        doctor.SetAction((parsed, _) => DoctorAsync(parsed.GetValue(config), parsed.GetValue(json)));
        return doctor;
    }

    private Command Sync()
    {
        var apply = new Option<bool>("--apply") { Description = "Write the planned changes (default is a dry run). The logind drop-in asks sudo for your password." };
        var config = ConfigOption();
        var json = new Option<bool>("--json") { Description = "Output the plan as JSON" };
        var sync = new Command(
            "sync",
            "Plan the differences from config/omarchy.json, and with --apply write them. Exits 1 when a change failed.")
        {
            apply, config, json,
        };
        sync.SetAction((parsed, cancellationToken) => SyncAsync(parsed.GetValue(apply), parsed.GetValue(config), parsed.GetValue(json), cancellationToken));
        return sync;
    }

    private async Task<int> DoctorAsync(string? config, bool json)
    {
        try
        {
            var policy = OmarchyPolicy.Load(PolicyPath(config));
            var state = OmarchyState.Read(host.Paths, proc);
            var checks = OmarchyPlan.Compare(policy, state);
            if (json)
            {
                await output.WriteLineAsync(Report(checks)).ConfigureAwait(false);
            }
            else
            {
                var table = new TextTable("Omarchy vs config/omarchy.json", [("Area", false), ("Setting", false), ("Have", false), ("Want", false), ("Status", false)]);
                foreach (var check in checks)
                {
                    _ = table.Add(check.Area, check.Setting, check.Have, check.Want, check.Status.Label());
                }

                await output.WriteAsync(table.Render()).ConfigureAwait(false);
                await output.WriteLineAsync(Footer(checks)).ConfigureAwait(false);
            }

            return checks.Any(check => check.Status == CheckStatus.Fail) ? (int)ExitCode.Drift : (int)ExitCode.Clean;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await host.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
    }

    private async Task<int> SyncAsync(bool apply, string? config, bool json, CancellationToken cancellationToken)
    {
        try
        {
            var policy = OmarchyPolicy.Load(PolicyPath(config));
            var state = OmarchyState.Read(host.Paths, proc);
            var actions = await OmarchySync.ApplyAsync(policy, state, host.Paths, proc, apply, cancellationToken).ConfigureAwait(false);
            if (json)
            {
                await output.WriteLineAsync(Actions(actions)).ConfigureAwait(false);
            }
            else if (actions.Count == 0)
            {
                await output.WriteLineAsync("This machine matches config/omarchy.json.").ConfigureAwait(false);
            }
            else
            {
                var mode = apply ? "APPLIED" : "DRY-RUN (pass --apply to execute)";
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

    private string PolicyPath(string? config) =>
        string.IsNullOrEmpty(config) ? host.PolicyFile : Path.GetFullPath(config);

    private static string Report(IReadOnlyList<OmarchyCheck> checks) =>
        new JsonArray([.. checks.Select(check => (JsonNode)new JsonObject
        {
            ["area"] = check.Area,
            ["setting"] = check.Setting,
            ["have"] = check.Have,
            ["want"] = check.Want,
            ["status"] = check.Status.Label(),
            ["fix"] = check.Fix,
        })]).ToJsonString(JsonOutputOptions.Indented);

    private static string Actions(IReadOnlyList<OmarchyAction> actions) =>
        new JsonArray([.. actions.Select(action => (JsonNode)new JsonObject
        {
            ["area"] = action.Check.Area,
            ["setting"] = action.Check.Setting,
            ["have"] = action.Check.Have,
            ["want"] = action.Check.Want,
            ["status"] = action.Status,
            ["detail"] = action.Detail,
        })]).ToJsonString(JsonOutputOptions.Indented);
}
