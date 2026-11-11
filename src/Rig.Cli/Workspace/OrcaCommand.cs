using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Orca;
using Rig.Core.Packages;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Cli.Workspace;

/// <summary>How <c>rig workspace orca</c> reaches Orca.</summary>
/// <param name="Locations">Where the instance keeps its files.</param>
/// <param name="Exchange">Talks to the running Orca's runtime socket.</param>
/// <param name="Error">Where the not-running notice goes.</param>
internal sealed record OrcaHost(OrcaLocations Locations, IOrcaExchange Exchange, TextWriter Error)
{
    /// <summary>This machine's Orca, over its real socket.</summary>
    public static OrcaHost Current { get; } = new(OrcaLocations.Current, new SocketOrcaExchange(), Console.Error);
}

/// <summary>
/// <c>rig workspace orca</c>: audit and sync the Orca install against <c>config/orca.json</c>. Ported from
/// <c>src/rig/workspace/orca_cli.py</c>.
/// </summary>
/// <param name="host">Where the estate and its registry are.</param>
/// <param name="orca">How Orca is reached.</param>
/// <param name="proc">Runs the <c>orca</c> CLI.</param>
/// <param name="time">The clock a settings copy is aged by.</param>
/// <param name="output">Where reports go.</param>
internal sealed class OrcaCommand(WorkspaceHost host, OrcaHost orca, IProc proc, TimeProvider time, TextWriter output)
{
    /// <summary>The exit code when Orca is not running; the scheduled sync retries it.</summary>
    public const int Unavailable = 3;

    private const string OrcaDataHelp =
        "Orca's user-data folder, e.g. %APPDATA%/orca-personal (default: the Orca that launched this terminal, else the platform's)";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The <c>orca</c> group.</summary>
    public Command Build()
    {
        var group = new Command("orca", "Manage Orca workspace configuration (agent flags, repos, hooks, base refs, colors).");
        group.Subcommands.Add(Doctor());
        group.Subcommands.Add(Sync());
        return group;
    }

    private static Option<string?> ConfigOption() => new("--config", "-c") { Description = "Path to orca.json" };

    private static Option<string?> RootOption() => new("--root") { Description = "Estate root holding the managed projects" };

    private static Option<string?> OrcaDataOption() => new("--orca-data") { Description = OrcaDataHelp };

    private Command Doctor()
    {
        var config = ConfigOption();
        var root = RootOption();
        var orcaData = OrcaDataOption();
        var json = new Option<bool>("--json") { Description = "Output findings as JSON" };
        var doctor = new Command("doctor", "Compare Orca's saved settings and live repos with config/orca.json. Read-only; exits 1 on any failure.")
        {
            config, root, orcaData, json,
        };
        doctor.SetAction((parsed, cancellationToken) =>
            DoctorAsync(parsed.GetValue(config), parsed.GetValue(root), parsed.GetValue(orcaData), parsed.GetValue(json), cancellationToken));
        return doctor;
    }

    private Command Sync()
    {
        var apply = new Option<bool>("--apply") { Description = "Apply planned changes (default is dry-run)" };
        var config = ConfigOption();
        var repo = new Option<string?>("--repo", "-r") { Description = "Sync only the named repo" };
        var root = RootOption();
        var orcaData = OrcaDataOption();
        var json = new Option<bool>("--json") { Description = "Output actions as JSON" };
        var sync = new Command(
            "sync",
            "Sync Orca's repos to config/orca.json (registration, colors, icons, scripts, refs). Each agent's launch line is sent through "
            + "Orca's runtime; agent sleep is listed as a manual step. Exits 3 when Orca is not running, 1 when any change failed.")
        {
            apply, config, repo, root, orcaData, json,
        };
        sync.SetAction((parsed, cancellationToken) => SyncAsync(
            new SyncArgs(parsed.GetValue(apply), parsed.GetValue(config), parsed.GetValue(repo), parsed.GetValue(root), parsed.GetValue(orcaData), parsed.GetValue(json)),
            cancellationToken));
        return sync;
    }

    private async Task<int> DoctorAsync(string? config, string? root, string? orcaData, bool json, CancellationToken cancellationToken)
    {
        var policyPath = PolicyPath(config);
        var userData = orca.Locations.UserData(orcaData);
        var (snapshot, _) = await SnapshotAsync(userData, cancellationToken).ConfigureAwait(false);
        var managed = Projects().Where(spec => spec.Enabled).Select(spec => spec.Name).ToList();
        var findings = OrcaDoctor.Diagnose(OrcaPolicy.Load(policyPath), snapshot, Paths(root), managed, time.GetUtcNow());

        if (json)
        {
            var rows = new JsonArray([.. findings.Select(f => (JsonNode)new JsonObject
            {
                ["subject"] = f.Subject,
                ["check"] = f.Check,
                ["status"] = f.Status.Label(),
                ["detail"] = f.Detail,
                ["fix"] = f.Fix,
            })]);
            await output.WriteLineAsync(rows.ToJsonString(Indented)).ConfigureAwait(false);
        }
        else
        {
            var table = new TextTable($"Orca vs {Path.GetFileName(policyPath)}", [("Subject", false), ("Check", false), ("Status", false), ("Detail", false)]);
            var last = string.Empty;
            foreach (var f in findings)
            {
                _ = table.Add(f.Subject != last ? f.Subject : string.Empty, f.Check, f.Status.Label(), f.Detail);
                last = f.Subject;
                if (f.Fix.Length > 0)
                {
                    _ = table.Add(string.Empty, string.Empty, string.Empty, "fix: " + f.Fix);
                }
            }

            await output.WriteAsync(table.Render()).ConfigureAwait(false);
        }

        return findings.Any(f => f.Status == CheckStatus.Fail) ? 1 : 0;
    }

    private async Task<int> SyncAsync(SyncArgs args, CancellationToken cancellationToken)
    {
        var policyPath = PolicyPath(args.Config);
        var policy = OrcaPolicy.Load(policyPath);
        var userData = orca.Locations.UserData(args.OrcaData);
        var (snapshot, live) = await SnapshotAsync(userData, cancellationToken).ConfigureAwait(false);
        if (snapshot.LaunchArgs is null)
        {
            await orca.Error.WriteLineAsync("Orca is not running, so there is nothing to sync; retry once it starts.").ConfigureAwait(false);
            return Unavailable;
        }

        List<SyncAction> actions = [];
        if (args.Repo is null)
        {
            actions.AddRange(OrcaSync.SettingsManualActions(policy, snapshot));
            actions.AddRange(await OrcaSync.SyncAgentLaunchAsync(policy, snapshot, args.Apply, live, cancellationToken).ConfigureAwait(false));
        }

        var retired = Projects().Where(spec => !spec.Enabled).Select(spec => spec.Name).ToHashSet(StringComparer.Ordinal);
        actions.AddRange(await OrcaSync.SyncReposAsync(
            policy, snapshot, Paths(args.Root), args.Apply, args.Repo, new OrcaCliRunner(proc, userData), live, retired, cancellationToken).ConfigureAwait(false));

        if (args.Json)
        {
            var rows = new JsonArray([.. actions.Select(a => (JsonNode)new JsonObject
            {
                ["repo"] = a.Repo,
                ["action"] = a.Action,
                ["field"] = a.Field,
                ["old_value"] = a.OldValue,
                ["new_value"] = a.NewValue,
                ["applied"] = a.Applied,
                ["detail"] = a.Detail,
            })]);
            await output.WriteLineAsync(rows.ToJsonString(Indented)).ConfigureAwait(false);
        }
        else if (actions.Count == 0)
        {
            await output.WriteLineAsync("All Orca repositories are in sync with policy.").ConfigureAwait(false);
            return 0;
        }
        else
        {
            var mode = args.Apply ? "APPLIED" : "DRY-RUN (pass --apply to execute)";
            var table = new TextTable(
                $"Orca Sync vs {Path.GetFileName(policyPath)} ({mode})",
                [("Repo", false), ("Action", false), ("Field", false), ("Current", false), ("Target", false), ("Status", false)]);
            foreach (var a in actions)
            {
                _ = table.Add(a.Repo, a.Action, a.Field, OrNone(a.OldValue), OrNone(a.NewValue), StatusOf(a));
                if (a.Detail.Length > 0 && !a.Applied && a.Detail != "planned")
                {
                    _ = table.Add(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, a.Detail);
                }
            }

            await output.WriteAsync(table.Render()).ConfigureAwait(false);
        }

        return actions.Any(a => a.Action == "error") ? 1 : 0;
    }

    private static string StatusOf(SyncAction action) =>
        action.Applied ? "applied" : action.Action is "error" or "manual" ? action.Action : "planned";

    private static string OrNone(string value) => value.Length > 0 ? value : "(none)";

    // The policy beside the registry: <checkout>/config/orca.json.
    private string PolicyPath(string? config) =>
        string.IsNullOrEmpty(config)
            ? Path.Join(Path.GetDirectoryName(host.ManagedProjectsFile) ?? string.Empty, Path.GetFileName(OrcaPolicy.RelativePath))
            : Path.GetFullPath(config);

    private IReadOnlyList<ManagedRepoSpec> Projects() => ManagedProjects.Load(host.ManagedProjectsFile);

    private OrcaPaths Paths(string? root)
    {
        var personal = PathResolution.Resolve(host.PersonalDir);
        var estate = PathResolution.Resolve(string.IsNullOrEmpty(root) ? personal : root);
        return new OrcaPaths(estate, Path.GetDirectoryName(personal) ?? personal, host.Home);
    }

    private async Task<(OrcaSnapshot Snapshot, OrcaLive Live)> SnapshotAsync(string? userData, CancellationToken cancellationToken)
    {
        var live = new OrcaLive(OrcaLive.TryConnect(orca.Locations.RuntimeMetadata(userData), orca.Exchange));
        var launch = await live.LaunchArgsAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await OrcaProfile.SnapshotAsync(orca.Locations.ProfileDir(userData), new OrcaCliRunner(proc, userData), launch, cancellationToken).ConfigureAwait(false);
        return (snapshot, live);
    }

    private sealed record SyncArgs(bool Apply, string? Config, string? Repo, string? Root, string? OrcaData, bool Json);
}
