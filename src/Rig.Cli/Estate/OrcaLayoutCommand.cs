using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Cli.Workspace;
using Rig.Core.Estate;
using Rig.Core.Orca;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate orca</c>: Orca's projects and groups against the <c>orca.group</c> each managed project declares, and,
/// with <c>--apply</c>, the changes that make them match. Ported from the Python <c>estate/orca_cli.py</c>.
/// </summary>
/// <param name="host">The code root, <c>~</c> in a path, the estate owners and this machine's skip list.</param>
/// <param name="orca">Where each Orca profile keeps its files, and the exchange that talks to it.</param>
/// <param name="proc">Runs lsof, so an archived project a process works in is not removed.</param>
/// <param name="github">Asks GitHub which repos are archived.</param>
/// <param name="output">Where the findings go.</param>
/// <param name="error">Where each change made, warnings and refusals go, so <c>--json</c> stays clean.</param>
internal sealed class OrcaLayoutCommand(EstateHost host, OrcaHost orca, IProc proc, IGitHubApi github, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The <c>orca</c> subcommand.</summary>
    public Command Build()
    {
        var config = new Option<string?>("--config", "-c") { Description = "Path to managed-projects.json" };
        var codeRoot = new Option<string?>("--code-root") { Description = "Folder holding the group folders (default ~/code)" };
        var userData = new Option<string?>("--orca-data")
        {
            Description = "The estate's Orca user-data folder (default: %APPDATA%\\orca-personal on Windows, Orca's own elsewhere)",
        };
        var workData = new Option<string?>("--work-orca-data")
        {
            Description = "The work Orca profile, kept free of estate repos (default: Orca's own folder on Windows; none elsewhere)",
        };
        var apply = new Option<bool>("--apply") { Description = "Register, create, move and delete to match, and clear estate repos from the work profile." };
        var repos = new Option<string[]>("--repo", "-r")
        {
            Description = "Act on and report only this managed project (repeatable). With --apply, registers and groups only "
                + "the named projects, deletes no empty group and leaves the work profile alone.",
            AllowMultipleArgumentsPerToken = false,
        };
        var json = new Option<bool>("--json") { Description = "Write the findings as JSON to stdout" };
        var command = new Command(
            "orca",
            "Orca's projects and groups against the `orca.group` each managed project declares.\n\n"
            + "Reports unregistered clones, projects in the wrong group, missing groups, empty undeclared groups, stale "
            + "registrations, and estate repos in Orca that managed-projects.json does not place. On Windows it also reports "
            + "estate repos in the work Orca profile. Read-only unless --apply, which registers clones, creates groups, moves "
            + "projects and deletes empty groups through the running Orca app, and removes estate repos (and the groups they "
            + "leave empty) from the work profile; what is left afterwards needs a config change or a clone. Projects in this "
            + "machine's `tools.estate.skip_repos` (`~/.workspace/config.json`) are never registered; one already in Orca is "
            + "reported as `skipped`, not removed.")
        {
            config, codeRoot, userData, workData, apply, repos, json,
        };
        command.SetAction((parsed, cancellationToken) => RunAsync(
            new(parsed.GetValue(config), parsed.GetValue(codeRoot), parsed.GetValue(userData), parsed.GetValue(workData), parsed.GetValue(apply), parsed.GetValue(repos) ?? [], parsed.GetValue(json)),
            cancellationToken));
        return command;
    }

    /// <summary>The findings as <c>estate orca --json</c> writes them.</summary>
    internal static string Dump(IEnumerable<LayoutFinding> findings) =>
        new JsonArray([.. findings.Select(f => (JsonNode)new JsonObject
        {
            ["kind"] = f.Kind,
            ["repo"] = f.Repo,
            ["path"] = f.Path,
            ["reason"] = f.Reason,
            ["suggested_action"] = f.SuggestedAction,
            ["command"] = f.Command,
        })]).ToJsonString(Indented);

    /// <summary>Whether a GitHub owner is one of the estate's: a personal owner or an org.</summary>
    internal static Func<string, bool> EstateOwners(string scopesFile)
    {
        var scopes = EstateScopes.Load(scopesFile);
        var owners = scopes.Owners.Concat(scopes.Orgs).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return owners.Contains;
    }

    private async Task<int> RunAsync(Args args, CancellationToken cancellationToken)
    {
        var root = string.IsNullOrEmpty(args.CodeRoot) ? host.CodeRoot : host.ExpandHome(args.CodeRoot);
        var estateDir = string.IsNullOrEmpty(args.OrcaData) ? orca.Locations.EstateUserData() : host.ExpandHome(args.OrcaData);
        var workDir = string.IsNullOrEmpty(args.WorkOrcaData) ? orca.Locations.WorkUserData() : host.ExpandHome(args.WorkOrcaData);
        var log = (string line) => error.WriteLine(line);
        List<LayoutFinding> findings;
        Func<string, bool> isEstateOwner;
        try
        {
            var declared = OrcaLayout.LoadDeclared(string.IsNullOrEmpty(args.Config) ? host.ManagedProjectsFile : Path.GetFullPath(host.ExpandHome(args.Config)));
            IReadOnlySet<string>? only = args.Repos.Length > 0 ? args.Repos.ToHashSet(StringComparer.Ordinal) : null;
            if (only is not null && only.Except(declared.Select(d => d.Name), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } unknown)
            {
                await error.WriteLineAsync($"not a managed project: {string.Join(", ", unknown)}".AsMemory(), cancellationToken).ConfigureAwait(false);
                return 2;
            }

            isEstateOwner = EstateOwners(host.ScopesFile);
            var skipped = MachineConfig.SkipRepos(host.MachineConfigFile);
            var runtime = OrcaRuntime.FromMetadata(orca.Locations.RuntimeMetadata(estateDir), orca.Exchange);
            var archived = await ArchivedAsync(declared, await LayoutSnapshot.ReadAsync(runtime, cancellationToken).ConfigureAwait(false), isEstateOwner, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<LayoutFinding> carried = [];
            if (args.Apply)
            {
                carried = await OrcaLayout.ApplyAsync(
                    runtime, declared, root, isEstateOwner, InUse(), log, archived: archived, skipped: skipped, only: only, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var state = await LayoutSnapshot.ReadAsync(runtime, cancellationToken).ConfigureAwait(false);
            findings = [.. OrcaLayout.Audit(declared, state, root, isEstateOwner, archived: archived, skipped: skipped)];
            findings.AddRange(carried.Where(c => !findings.Contains(c)).ToList());
            if (only is not null)
            {
                findings = [.. OrcaLayout.Scoped(findings, declared, only)];
            }
        }
        catch (OrcaUnavailableException e)
        {
            await error.WriteLineAsync(e.Message.AsMemory(), cancellationToken).ConfigureAwait(false);
            return OrcaCommand.Unavailable;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(e.Message.AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        if (args.Repos.Length == 0 && workDir is not null && !PathResolution.Same(PathResolution.Resolve(workDir), PathResolution.Resolve(estateDir)))
        {
            findings.AddRange(await WorkProfileAsync(workDir, args.Apply, isEstateOwner, log, cancellationToken).ConfigureAwait(false));
        }

        if (args.Json)
        {
            await output.WriteLineAsync(Dump(findings).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (findings.Count == 0)
        {
            await output.WriteLineAsync("Orca matches managed-projects.json".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable(
            $"{findings.Count} difference(s) from the declared Orca layout",
            [("kind", false), ("repo", false), ("path", false), ("reason", false), ("action", false), ("command", false)]);
        foreach (var f in findings)
        {
            _ = table.Add(f.Kind, f.Repo, f.Path, f.Reason, f.SuggestedAction, f.Command);
        }

        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    // Whether a process works in a folder; null where that cannot be known. lsof runs once, and only when asked.
    private Func<string, Task<bool?>> InUse()
    {
        Busy? busy = null;
        return async path =>
        {
            busy ??= await Busy.SnapshotAsync(proc).ConfigureAwait(false);
            return busy.Dirs is null ? null : Busy.InUse(path, busy.Dirs);
        };
    }

    // Which declared or registered estate repos GitHub reports archived; warns when it cannot be asked.
    private async Task<IReadOnlySet<string>> ArchivedAsync(
        IReadOnlyList<DeclaredProject> declared, LayoutSnapshot state, Func<string, bool> isEstateOwner, CancellationToken cancellationToken)
    {
        var registered = state.Registrations.Select(r => r.Slug).OfType<string>().Where(slug => slug.Length > 0 && isEstateOwner(slug.Split('/')[0]));
        var found = await GitHubArchived.SlugsAsync(github, declared.Select(p => p.Slug).Concat(registered), cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            await error.WriteLineAsync("GitHub could not be asked which repos are archived; only archived-projects.json counts".AsMemory(), cancellationToken).ConfigureAwait(false);
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return found;
    }

    // The work profile's estate repos, removed first with --apply; skipped with a warning when it is not running.
    private async Task<IReadOnlyList<LayoutFinding>> WorkProfileAsync(string workDir, bool apply, Func<string, bool> isEstateOwner, Action<string> log, CancellationToken cancellationToken)
    {
        try
        {
            var runtime = OrcaRuntime.FromMetadata(orca.Locations.RuntimeMetadata(workDir), orca.Exchange);
            if (apply)
            {
                await OrcaLayout.ApplyWorkAsync(runtime, isEstateOwner, log, cancellationToken).ConfigureAwait(false);
            }

            return OrcaLayout.AuditWork(await LayoutSnapshot.ReadAsync(runtime, cancellationToken).ConfigureAwait(false), isEstateOwner);
        }
        catch (OrcaUnavailableException e)
        {
            await error.WriteLineAsync($"work Orca profile not checked: {e.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return [];
        }
    }

    private sealed record Args(string? Config, string? CodeRoot, string? OrcaData, string? WorkOrcaData, bool Apply, string[] Repos, bool Json);
}
