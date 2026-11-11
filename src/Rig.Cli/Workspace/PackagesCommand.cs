using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Packages;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Cli.Workspace;

/// <summary>
/// <c>rig workspace packages</c> (alias <c>artifacts</c>): track and audit the npm and NuGet packages the estate publishes.
/// Ported from <c>src/rig/workspace/packages_cli.py</c>.
/// </summary>
/// <param name="host">Where it looks.</param>
/// <param name="proc">Runs git, gh and npm.</param>
/// <param name="http">Reads the registries.</param>
/// <param name="time">The clock ages and the audit read.</param>
/// <param name="output">Where reports go.</param>
internal sealed class PackagesCommand(WorkspaceHost host, IProc proc, HttpClient http, TimeProvider time, TextWriter output)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly Dictionary<string, string> Badges = new(StringComparer.Ordinal)
    {
        [PackageDashboard.UnreleasedChanges] = "unreleased",
        [PackageDashboard.TagWithoutRegistry] = "unreleased tag",
        [PackageDashboard.RegistryWithoutTag] = "no tag",
        [PackageDashboard.UnlistedFlag] = "unlisted",
        [PackageDashboard.DeprecatedFlag] = "deprecated",
        [PackageDashboard.VersionFamilyDrift] = "drift",
        [PackageDashboard.RegistryUnavailable] = "registry unavailable",
        [PackageDashboard.TagsUnavailable] = "tags unavailable",
    };

    /// <summary>The <c>packages</c> group: with no subcommand it runs <c>status</c>.</summary>
    public Command Build()
    {
        var (config, project, json) = Common("Output tracking records as JSON");
        var packages = new Command("packages", "Track and audit published npm/NuGet packages against the GitHub repos that publish them.")
        {
            config, project, json,
        };
        packages.Aliases.Add("artifacts");
        packages.SetAction((parsed, cancellationToken) =>
            StatusAsync(parsed.GetValue(config), parsed.GetValue(project), parsed.GetValue(json), cancellationToken));
        packages.Subcommands.Add(Status("status", "Track deployed packages, local versions, registry releases, and sync status."));
        packages.Subcommands.Add(Status("list", "Alias for 'workspace packages status'."));
        packages.Subcommands.Add(Audit());
        packages.Subcommands.Add(Dashboard());
        return packages;
    }

    private static (Option<string?> Config, Option<string?> Project, Option<bool> Json) Common(string json) =>
        (new("--config", "-c") { Description = "Path to managed-projects.json registry file" },
         new("--project", "-p") { Description = "Only this project" },
         new("--json") { Description = json });

    private Command Status(string name, string description)
    {
        var (config, project, json) = Common("Output tracking records as JSON");
        var status = new Command(name, description) { config, project, json };
        status.SetAction((parsed, cancellationToken) =>
            StatusAsync(parsed.GetValue(config), parsed.GetValue(project), parsed.GetValue(json), cancellationToken));
        return status;
    }

    private Command Audit()
    {
        var (config, project, json) = Common("Output findings as JSON");
        var trust = new Option<bool>("--trust")
        {
            Description = "Also read npm trust configs (`npm trust list`; prompts for a 2FA code) "
                + "and print every NuGet policy's checklist, not only those that look missing or stale",
        };
        var strict = new Option<bool>("--strict") { Description = "Exit non-zero on any failing check" };
        var audit = new Command("audit", AuditHelp) { config, project, trust, json, strict };
        audit.SetAction((parsed, cancellationToken) => AuditAsync(
            parsed.GetValue(config), parsed.GetValue(project), parsed.GetValue(trust), parsed.GetValue(json), parsed.GetValue(strict), cancellationToken));
        return audit;
    }

    private Command Dashboard()
    {
        var root = new Option<string?>("--root") { Description = "Code root to scan" };
        var (config, project, json) = Common("Emit inventory as JSON for a machine");
        var dashboard = new Command(
            "dashboard", "Every published package with its registry version, matching git tag and unreleased changelog entries.")
        {
            root, config, project, json,
        };
        dashboard.SetAction((parsed, cancellationToken) => DashboardAsync(
            parsed.GetValue(root), parsed.GetValue(config), parsed.GetValue(project), parsed.GetValue(json), cancellationToken));
        return dashboard;
    }

    private const string AuditHelp = """
        Check each declared package's manifest, workflow, registry provenance and publish runs.

        Packages are the `packages` entries in config/managed-projects.json. Checks:

        - repo: the declared repo redirects, so it was moved.
        - manifest: its repository URL names another repo. Fails for npm, which refuses the publish with a 422; warns for NuGet.
        - version: warns when the manifest is ahead of the registry's latest.
        - workflow: `auth: oidc` without `id-token: write`; warns on a token secret.
        - login (NuGet): no job runs NuGet/login (naming any reusable workflow it calls, whose file the policy must name
          instead), that job lacks `id-token: write`, or its `user:` differs from `nuget_user`.
        - environment (NuGet): the login job's `environment:` differs from the declared `environment`.
        - push (NuGet): warns when `nuget push` is missing, reads a secrets.NUGET_* key, or lacks --skip-duplicate, so a
          rerun after a partial publish fails.
        - trusted-publisher: warns when npm's latest was published with a token.
        - provenance: the last publish recorded a different repo id or owner, so the repo moved since the last publish.
        - registry (NuGet): warns when the package does not exist (the first publish needs a push-new policy) or every
          version is unlisted.
        - owners (NuGet): warns when search owners lack `nuget_owner`. Search reindexes only on a publish, so confirm on
          the package's Manage page.
        - listed (NuGet): warns when the newest listed version is a CI build.
        - vulnerabilities (NuGet): fails when the newest listed version has an advisory.
        - deprecation (NuGet): warns when the newest listed version is deprecated.
        - trust: --trust only. The npm trust config does not name the repo and workflow.
        - policy (NuGet): not a check. The fields to enter on nuget.org, printed when registry or provenance suggests the
          policy is missing, and always under --trust.
        - runs: the latest completed publish runs failed.

        See docs/packages/README.md for the publishing runbooks.
        """;

    private string ConfigFile(string? config) => string.IsNullOrEmpty(config) ? host.ManagedProjectsFile : PathResolution.Resolve(config);

    private static List<PackageBinding> Bindings(string configFile, string? project) =>
        [.. PackageBindings.Load(configFile).Where(b => string.IsNullOrEmpty(project) || string.Equals(b.Project, project, StringComparison.OrdinalIgnoreCase))];

    private LivePackageFetcher Fetcher() => new(http, new GhApi(proc), proc);

    private async Task<int> StatusAsync(string? config, string? project, bool json, CancellationToken cancellationToken)
    {
        var bindings = Bindings(ConfigFile(config), project);
        var estate = PathResolution.Resolve(host.PersonalDir);
        var scopes = EstateScopes.Load(host.ScopesFile);
        var fetcher = Fetcher();
        var records = new List<PackageTrackingRecord>(bindings.Count);
        foreach (var binding in bindings)
        {
            var dir = scopes.ProjectDir(estate, binding.Project, binding.Repo);
            records.Add(await PackageTracking.TrackAsync(binding, fetcher, dir, cancellationToken).ConfigureAwait(false));
        }

        if (json)
        {
            var payload = new JsonArray([.. records.Select(r => (JsonNode)new JsonObject
            {
                ["project"] = r.Project,
                ["package"] = r.Package,
                ["registry"] = r.Registry,
                ["manifest_path"] = r.ManifestPath,
                ["local_version"] = r.LocalVersion,
                ["latest_version"] = r.LatestVersion,
                ["latest_stable_version"] = r.StableVersion,
                ["deployed_version"] = r.LatestVersion,
                ["status"] = r.Status.Label(),
                ["published_at"] = Iso(r.LatestPublished),
                ["latest_published_at"] = Iso(r.LatestPublished),
                ["stable_published_at"] = Iso(r.StablePublished),
                ["registry_url"] = r.RegistryUrl,
                ["detail"] = r.Detail,
            })]);
            await output.WriteLineAsync(payload.ToJsonString(Indented)).ConfigureAwait(false);
            return 0;
        }

        var now = time.GetUtcNow();
        var table = new TextTable(
            $"Deployed Packages & Artifacts ({records.Count})",
            [("Project", false), ("Package", false), ("Registry", false), ("Local", false), ("Latest", false), ("Stable", false),
             ("Status", false), ("Published", false), ("Registry URL", false)]);
        foreach (var r in records)
        {
            var published = r.LatestPublished is { } at
                ? $"{at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({PackageTracking.RelativeAge(at, now)})"
                : "--";
            table.Add(r.Project, r.Package, r.Registry, r.LocalVersion ?? "--", r.LatestVersion ?? "--", r.StableVersion ?? "--",
                r.Status.Label(), published, r.RegistryUrl);
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> AuditAsync(string? config, string? project, bool trust, bool json, bool strict, CancellationToken cancellationToken)
    {
        var bindings = Bindings(ConfigFile(config), project);
        var findings = await PackageAudit.AuditAsync(bindings, Fetcher(), time.GetUtcNow(), trust, cancellationToken).ConfigureAwait(false);
        var exit = strict && findings.Any(f => f.Status == CheckStatus.Fail) ? 1 : 0;
        if (json)
        {
            var payload = new JsonArray([.. findings.Select(f => (JsonNode)new JsonObject
            {
                ["package"] = f.Package,
                ["check"] = f.Check,
                ["status"] = f.Status.Label(),
                ["detail"] = f.Detail,
                ["fix"] = f.Fix,
            })]);
            await output.WriteLineAsync(payload.ToJsonString(Indented)).ConfigureAwait(false);
            return exit;
        }

        var table = new TextTable($"Published packages ({bindings.Count})", [("Package", false), ("Check", false), ("Status", false), ("Detail", false)]);
        var last = string.Empty;
        foreach (var f in findings)
        {
            table.Add(f.Package == last ? string.Empty : f.Package, f.Check, f.Status.Label(), f.Detail);
            last = f.Package;
            if (f.Fix.Length > 0)
            {
                table.Add(string.Empty, string.Empty, string.Empty, $"fix: {f.Fix}");
            }
        }

        var text = new StringBuilder(table.Render());
        if (!trust && bindings.Any(b => b.Spec.IsNpm && b.Spec.IsOidc))
        {
            _ = text.AppendLine("npm trust configs not read; pass --trust (needs a 2FA code).");
        }

        if (!trust && bindings.Any(b => b.Spec.Registry == PackageSpec.NuGet && b.Spec.IsOidc))
        {
            _ = text.AppendLine("NuGet policies have no API; pass --trust to print every policy's checklist.");
        }

        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        return exit;
    }

    private async Task<int> DashboardAsync(string? root, string? config, string? project, bool json, CancellationToken cancellationToken)
    {
        var configFile = ConfigFile(config);
        var bindings = Bindings(configFile, project);
        var groups = ManagedProjects.Load(configFile)
            .Where(spec => !string.IsNullOrEmpty(spec.OrcaGroup))
            .GroupBy(spec => spec.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().OrcaGroup!, StringComparer.Ordinal);
        var codeRoot = Path.GetDirectoryName(PathResolution.Resolve(host.PersonalDir)) ?? host.PersonalDir;
        var rows = await PackageDashboard.CollectAsync(
            bindings,
            new LiveRegistryInfo(http),
            new Git(proc, codeRoot),
            string.IsNullOrEmpty(root) ? null : PathResolution.Resolve(Home(root)),
            codeRoot,
            groups,
            cancellationToken).ConfigureAwait(false);

        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(rows, Indented)).ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable(
            $"Estate — Published Packages ({rows.Count})",
            [("project", false), ("package", false), ("registry", false), ("latest", false), ("tag", false), ("changelog", false), ("status", false)]);
        foreach (var r in rows)
        {
            var changelog = r.ChangelogUnreleased ? "unreleased" : r.ChangelogFound ? "clean" : "—";
            var status = r.Flags.Count == 0 ? r.Status : string.Join(' ', r.Flags.Select(flag => Badges.GetValueOrDefault(flag, $"[{flag}]")));
            table.Add(r.Project, r.Package, r.Registry, r.RegistryVersion ?? "—", r.LatestTag ?? "—", changelog, status);
        }

        var synced = rows.Count(r => r.Status == PackageDashboard.Synced);
        var unpublished = rows.Count(r => r.Status == PackageDashboard.Unpublished);
        var text = new StringBuilder(table.Render())
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{rows.Count} packages · {synced} synced · {rows.Count - synced - unpublished} flagged · {unpublished} unpublished")
            .AppendLine()
            .AppendLine("`workspace packages dashboard --json` for machine-readable output");
        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        return 0;
    }

    private string Home(string path) =>
        path == "~" ? host.Home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Join(host.Home, path[2..]) : path;

    private static string? Iso(DateTimeOffset? at) => at?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
