using System.Globalization;
using System.Text.RegularExpressions;
using Rig.Core.GitHub;

namespace Rig.Core.Packages;

/// <summary>
/// Audits every declared package against GitHub and its registry: the repo still resolves to the declared name, the
/// manifest points at it, the publish workflow can mint an OIDC token, the registry's latest release was built from it,
/// and the latest publish runs succeeded. NuGet policies have no API, so each one is printed as a checklist when asked
/// for or when the findings suggest it drifted.
/// </summary>
public static partial class PackageAudit
{
    // Findings that suggest a policy is missing or names an old owner: the package does not exist or is all unlisted,
    // or its last publish recorded another repo. Owners are left out because search's list goes stale between publishes.
    private static readonly HashSet<string> PolicyDrift = new(["registry", "provenance"], StringComparer.Ordinal);

    /// <summary>Every binding's findings, in order, with the NuGet policy checklists after each policy's last package.</summary>
    public static async Task<IReadOnlyList<Finding>> AuditAsync(
        IReadOnlyList<PackageBinding> bindings, IPackageFetcher fetcher, DateTimeOffset now, bool trust, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var perBinding = new List<List<Finding>>(bindings.Count);
        foreach (var binding in bindings)
        {
            perBinding.Add(await AuditBindingAsync(binding, fetcher, now, trust, cancellationToken).ConfigureAwait(false));
        }

        AddPolicyChecklists(bindings, perBinding, trust);
        return [.. perBinding.SelectMany(findings => findings)];
    }

    /// <summary>One binding's findings.</summary>
    public static async Task<List<Finding>> AuditBindingAsync(
        PackageBinding binding, IPackageFetcher fetcher, DateTimeOffset now, bool trust, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(fetcher);
        var spec = binding.Spec;
        var report = new Report($"{spec.Registry}:{spec.Name}");
        var declared = Slug(binding.Repo);
        var live = declared is null ? null : await fetcher.GitHubRepoAsync(declared, cancellationToken).ConfigureAwait(false);
        if (declared is null || live is null)
        {
            report.Add("repo", CheckStatus.Fail, $"cannot resolve {binding.Repo} on GitHub");
            return report.Findings;
        }

        var canonical = live.FullName;
        if (Same(declared, canonical))
        {
            report.Add("repo", CheckStatus.Ok, canonical);
        }
        else
        {
            report.Add(
                "repo",
                CheckStatus.Fail,
                $"managed-projects.json says {declared}; GitHub redirects to {canonical}",
                $"set {binding.Project}'s repo to git@github.com:{canonical}.git");
        }

        var release = await fetcher.RegistryReleaseAsync(spec, cancellationToken).ConfigureAwait(false);
        await CheckManifestAsync(report, spec, fetcher, canonical, release, now, cancellationToken).ConfigureAwait(false);
        var workflow = await CheckWorkflowAsync(report, spec, fetcher, canonical, cancellationToken).ConfigureAwait(false);
        if (spec.IsNpm)
        {
            CheckNpmRelease(report, spec, release, live);
            if (trust)
            {
                await CheckNpmTrustAsync(report, spec, fetcher, canonical, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            CheckNuGetLogin(report, spec, workflow);
            CheckNuGetRelease(report, spec, release, canonical);
        }

        await CheckRunsAsync(report, spec, fetcher, canonical, cancellationToken).ConfigureAwait(false);
        return report.Findings;
    }

    /// <summary>Failures since the last success, newest first; cancelled and skipped runs neither break nor extend it.</summary>
    public static int ConsecutiveFailures(IEnumerable<string> conclusions)
    {
        ArgumentNullException.ThrowIfNull(conclusions);
        var count = 0;
        foreach (var conclusion in conclusions)
        {
            if (conclusion == "success")
            {
                break;
            }

            if (conclusion is "failure" or "timed_out" or "startup_failure")
            {
                count++;
            }
        }

        return count;
    }

    /// <summary><c>owner/name</c> of a GitHub remote in any form; null for anything else.</summary>
    internal static string? Slug(string? remote) => RepoSlug.FromRemote(remote)?.FullName;

    private static bool Same(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static async Task CheckManifestAsync(
        Report report, PackageSpec spec, IPackageFetcher fetcher, string canonical, Release? release, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var text = await fetcher.GitHubFileAsync(canonical, spec.Manifest, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            report.Add("manifest", CheckStatus.Fail, $"{spec.Manifest} not found on the default branch");
            return;
        }

        var (url, version) = ManifestFields.Read(spec.Registry, text);
        if (url is null)
        {
            report.Add("manifest", CheckStatus.Warn, $"{spec.Manifest} declares no repository URL");
        }
        else
        {
            var slug = Slug(url);
            if (spec.IsNpm ? slug == canonical : Same(slug, canonical))
            {
                report.Add("manifest", CheckStatus.Ok, url);
            }
            else
            {
                // npm verifies provenance against repository.url server-side and refuses the publish with a 422;
                // NuGet only ships the wrong source link.
                var consequence = spec.IsNpm ? "npm will reject the publish (422, provenance)" : "wrong source link";
                report.Add(
                    "manifest",
                    spec.IsNpm ? CheckStatus.Fail : CheckStatus.Warn,
                    $"{spec.Manifest} points at {url}: {consequence}",
                    $"set the repository URL to https://github.com/{canonical}.git");
            }
        }

        if (version is null || release is null)
        {
            return;
        }

        if (PackageVersions.IsAhead(version, release.Version))
        {
            var age = release.Published is { } published
                ? string.Create(CultureInfo.InvariantCulture, $", published {(long)Math.Floor((now - published).TotalDays)} days ago")
                : string.Empty;
            report.Add("version", CheckStatus.Warn, $"manifest is at {version}; the registry's latest is {release.Version}{age}");
        }
        else
        {
            report.Add("version", CheckStatus.Ok, $"{release.Version} published");
        }
    }

    // The file-wide checks; returns the workflow's text for the registry-specific ones.
    private static async Task<string?> CheckWorkflowAsync(
        Report report, PackageSpec spec, IPackageFetcher fetcher, string canonical, CancellationToken cancellationToken)
    {
        var path = $".github/workflows/{spec.Workflow}";
        var text = await fetcher.GitHubFileAsync(canonical, path, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            report.Add("workflow", CheckStatus.Fail, $"{path} not found on the default branch");
            return null;
        }

        var secrets = string.Join(", ", TokenSecretPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (!spec.IsOidc)
        {
            report.Add(
                "workflow",
                CheckStatus.Warn,
                $"publishes with a long-lived token ({(secrets.Length == 0 ? "secret" : secrets)})",
                "move to trusted publishing and set auth to oidc");
        }
        else if (!IdTokenWritePattern().IsMatch(text))
        {
            report.Add("workflow", CheckStatus.Fail, $"{path} lacks `id-token: write`; OIDC publishing cannot work");
        }
        else if (secrets.Length > 0)
        {
            report.Add("workflow", CheckStatus.Warn, $"OIDC enabled but {secrets} still referenced");
        }
        else
        {
            report.Add("workflow", CheckStatus.Ok, $"{path} has id-token: write");
        }

        return text;
    }

    private static string EnvFlag(PackageSpec spec) => spec.Environment is null ? string.Empty : $" --env {spec.Environment}";

    private static void CheckNpmRelease(Report report, PackageSpec spec, Release? release, RepoIdentity live)
    {
        if (release is null)
        {
            report.Add(
                "registry",
                CheckStatus.Warn,
                "not on npm",
                "a new package must exist before `npm trust`: publish once with 2FA, then run `npm trust github`");
            return;
        }

        if (spec.IsOidc && release.TrustedPublisher == false)
        {
            report.Add("trusted-publisher", CheckStatus.Warn, $"{release.Version} was published with a token, not OIDC");
        }
        else if (release.TrustedPublisher == true)
        {
            report.Add("trusted-publisher", CheckStatus.Ok, $"{release.Version} published via OIDC");
        }

        if (release.Provenance is not { } provenance)
        {
            report.Add("provenance", spec.IsOidc ? CheckStatus.Warn : CheckStatus.Skip, $"{release.Version} carries no provenance");
            return;
        }

        var builtFrom = Slug(provenance.RepositoryUrl);
        var trustFix =
            $"npm trust revoke {spec.Name} --id <id>; npm trust github {spec.Name} --repo {live.FullName} --file {spec.Workflow} --allow-publish{EnvFlag(spec)}";
        if (provenance.RepositoryId is { } repositoryId && repositoryId != live.Id)
        {
            report.Add(
                "provenance",
                CheckStatus.Fail,
                string.Create(CultureInfo.InvariantCulture, $"{release.Version} was built by a different repository ({builtFrom}, id {repositoryId})"));
        }
        else if (provenance.OwnerId is { } ownerId && ownerId != live.OwnerId)
        {
            report.Add(
                "provenance",
                CheckStatus.Fail,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"repository moved since {release.Version} was published: {builtFrom} -> {live.FullName} (owner id {ownerId} -> {live.OwnerId}); the trust config still names the old owner"),
                trustFix);
        }
        else if (!Same(builtFrom, live.FullName))
        {
            report.Add("provenance", CheckStatus.Fail, $"{release.Version} built from {builtFrom}", trustFix);
        }
        else
        {
            report.Add("provenance", CheckStatus.Ok, $"{release.Version} built from {builtFrom}/{provenance.WorkflowPath}");
        }
    }

    private static async Task CheckNpmTrustAsync(Report report, PackageSpec spec, IPackageFetcher fetcher, string canonical, CancellationToken cancellationToken)
    {
        var listing = await fetcher.NpmTrustAsync(spec.Name, cancellationToken).ConfigureAwait(false);
        if (listing.Error is { } error)
        {
            report.Add("trust", CheckStatus.Warn, $"npm trust list failed: {error}");
            return;
        }

        var matched = listing.Configs.Any(config =>
            Same(config.Repository, canonical)
            && string.Equals(NuGetGovernance.FileName(config.File), NuGetGovernance.FileName(spec.Workflow), StringComparison.OrdinalIgnoreCase)
            && config.Environment == spec.Environment);
        if (matched)
        {
            report.Add("trust", CheckStatus.Ok, $"trusts {canonical} {spec.Workflow}");
            return;
        }

        var seen = listing.Configs.Count == 0 ? "nothing" : string.Join(", ", listing.Configs.Select(config => $"{config.Repository} {config.File}"));
        report.Add(
            "trust",
            CheckStatus.Fail,
            $"trust config names {seen}; expected {canonical} {spec.Workflow}",
            $"npm trust github {spec.Name} --repo {canonical} --file {spec.Workflow} --allow-publish{EnvFlag(spec)}");
    }

    // The job a policy has to match: it runs NuGet/login as the policy owner, in its environment.
    private static void CheckNuGetLogin(Report report, PackageSpec spec, string? text)
    {
        if (text is null || !spec.IsOidc)
        {
            return;
        }

        var workflow = NuGetGovernance.ReadPublishWorkflow(text);
        if (workflow is null || workflow.LoginJobs.Count == 0)
        {
            var called = workflow?.Called ?? [];
            var hint = called.Count == 0 ? string.Empty : $"; it calls {string.Join(", ", called)}, and the policy must name the called file";
            report.Add("login", CheckStatus.Fail, $"no job in {spec.Workflow} runs NuGet/login{hint}");
            return;
        }

        var job = workflow.LoginJobs[0];
        var where = $"job {job.Name}";
        if (!job.IdTokenWrite)
        {
            report.Add("login", CheckStatus.Fail, $"{where} runs NuGet/login without `id-token: write`");
        }
        else if (job.User is null)
        {
            report.Add("login", CheckStatus.Warn, $"{where} sends user {(string.IsNullOrEmpty(job.UserInput) ? "(none)" : job.UserInput)}: no literal to check");
        }
        else if (spec.NuGetUser is null)
        {
            report.Add("login", CheckStatus.Skip, $"{where} logs in as {job.User}; declare nuget_user to check it");
        }
        else if (!Same(job.User, spec.NuGetUser))
        {
            report.Add(
                "login",
                CheckStatus.Fail,
                $"{where} logs in as {job.User}; expected {spec.NuGetUser}",
                $"set NuGet/login's user to {spec.NuGetUser} (the nuget.org profile name, not an email)");
        }
        else
        {
            report.Add("login", CheckStatus.Ok, $"{where} logs in as {job.User} with id-token: write");
        }

        if (spec.Environment is not null && !Same(job.Environment, spec.Environment))
        {
            report.Add(
                "environment",
                CheckStatus.Fail,
                $"{where} runs in {(string.IsNullOrEmpty(job.Environment) ? "no environment" : job.Environment)}; the policy requires {spec.Environment}");
        }
        else if (spec.Environment is not null)
        {
            report.Add("environment", CheckStatus.Ok, $"{where} runs in {spec.Environment}");
        }
        else if (job.Environment is not null)
        {
            report.Add("environment", CheckStatus.Skip, $"{where} runs in {job.Environment}; declare it if the policy requires it");
        }

        if (!job.PushFound)
        {
            report.Add("push", CheckStatus.Warn, $"{where} has no `nuget push` after NuGet/login; its key lasts one hour");
        }
        else if (job.PushSecrets.Count > 0)
        {
            report.Add("push", CheckStatus.Warn, $"push still reads {string.Join(", ", job.PushSecrets)}; use NuGet/login's key");
        }
        else if (!job.SkipDuplicate)
        {
            report.Add("push", CheckStatus.Warn, "push lacks --skip-duplicate, so a rerun after a partial publish fails");
        }
        else
        {
            report.Add("push", CheckStatus.Ok, "pushes with NuGet/login's key and --skip-duplicate");
        }
    }

    private static void CheckNuGetRelease(Report report, PackageSpec spec, Release? release, string canonical)
    {
        if (release is null)
        {
            report.Add("registry", CheckStatus.Warn, "not on nuget.org: the first publish needs a policy with the push-new scope", NuGetGovernance.TrustUrl);
            return;
        }

        if (release.Listed)
        {
            report.Add("registry", CheckStatus.Ok, $"{release.Version} is the newest listed version");
        }
        else
        {
            report.Add("registry", CheckStatus.Warn, $"every version is unlisted (newest {release.Version})");
        }

        CheckNuGetOwners(report, spec, release);
        if (!release.Listed)
        {
            return;
        }

        if (release.SourceRepoUrl is null)
        {
            report.Add("provenance", CheckStatus.Skip, $"{release.Version} records no source repository");
        }
        else if (Same(Slug(release.SourceRepoUrl), canonical))
        {
            report.Add("provenance", CheckStatus.Ok, $"{release.Version} built from {canonical}");
        }
        else
        {
            // The catalog copies the nuspec, so this is the manifest's claim at publish time, not the build's identity.
            // It still dates the last publish to the old name, and a policy made then matches neither name nor owner ID now.
            report.Add(
                "provenance",
                CheckStatus.Warn,
                $"{release.Version} records {Slug(release.SourceRepoUrl)}; the repo is now {canonical}. "
                + "A policy made for the old name will not match: NuGet checks owner and repo by name, and locks their IDs on the first publish",
                $"add a policy for {canonical} at {NuGetGovernance.TrustUrl}, publish, then delete the old one");
        }

        if (CiBuildPattern().IsMatch(release.Version))
        {
            report.Add(
                "listed",
                CheckStatus.Warn,
                $"the newest listed version, {release.Version}, is a CI build",
                $"unlist it at https://www.nuget.org/packages/{spec.Name}/{release.Version.Split('+', 2)[0]}/Manage");
        }

        if (release.Vulnerabilities.Count > 0)
        {
            report.Add("vulnerabilities", CheckStatus.Fail, $"{release.Version} has advisories: {string.Join(", ", release.Vulnerabilities)}");
        }

        if (release.Deprecation is not null)
        {
            report.Add("deprecation", CheckStatus.Warn, $"{release.Version} is deprecated: {release.Deprecation}");
        }
    }

    // Search reindexes on a publish, not on an owner change, so its list can be stale: an organisation added since the
    // last publish is missing. Advisory only, never a failure.
    private static void CheckNuGetOwners(Report report, PackageSpec spec, Release release)
    {
        if (release.Owners is not { } owners)
        {
            report.Add("owners", CheckStatus.Skip, "could not read owners from NuGet search");
            return;
        }

        var live = owners.Count == 0 ? "nobody" : string.Join(", ", owners);
        var expected = spec.Owners.Select(owner => owner.ToLowerInvariant());
        if (spec.NuGetOwner is not null)
        {
            expected = expected.Append(spec.NuGetOwner.ToLowerInvariant());
        }

        var have = owners.Select(owner => owner.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Where(owner => !have.Contains(owner)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            report.Add(
                "owners",
                CheckStatus.Warn,
                $"search lists {live} (as of the last publish); missing {string.Join(", ", missing)}",
                $"confirm at https://www.nuget.org/packages/{spec.Name}/Manage");
        }
        else
        {
            report.Add("owners", CheckStatus.Ok, $"search lists {live} (as of the last publish)");
        }
    }

    private static async Task CheckRunsAsync(Report report, PackageSpec spec, IPackageFetcher fetcher, string canonical, CancellationToken cancellationToken)
    {
        var conclusions = await fetcher.WorkflowConclusionsAsync(canonical, spec.Workflow, 20, cancellationToken).ConfigureAwait(false);
        if (conclusions is null)
        {
            report.Add("runs", CheckStatus.Skip, "could not read workflow runs");
            return;
        }

        var failed = ConsecutiveFailures(conclusions);
        if (failed > 0)
        {
            report.Add("runs", CheckStatus.Fail, string.Create(CultureInfo.InvariantCulture, $"last {failed} completed publish run(s) failed"));
        }
        else if (conclusions.Count > 0)
        {
            report.Add("runs", CheckStatus.Ok, $"latest publish run: {conclusions[0]}");
        }
        else
        {
            report.Add("runs", CheckStatus.Skip, "no completed publish runs");
        }
    }

    // One checklist per NuGet policy, after its last package, when asked for or when drift suggests it. Bindings that
    // share every policy field share a policy.
    private static void AddPolicyChecklists(IReadOnlyList<PackageBinding> bindings, List<List<Finding>> perBinding, bool trust)
    {
        var groups = new List<(string Key, PolicyChecklist Policy, List<int> Members)>();
        for (var i = 0; i < bindings.Count; i++)
        {
            var spec = bindings[i].Spec;
            if (spec.IsNpm || !spec.IsOidc)
            {
                continue;
            }

            var policy = Policy(bindings[i]);
            var key = string.Join(
                '\u001f',
                policy.PolicyOwner ?? "\u0000",
                policy.RepositoryOwner,
                policy.Repository,
                policy.Workflow,
                policy.Environment ?? "\u0000",
                string.Join('\u001e', policy.Scopes));
            var group = groups.FindIndex(g => g.Key == key);
            if (group < 0)
            {
                groups.Add((key, policy, [i]));
            }
            else
            {
                groups[group].Members.Add(i);
            }
        }

        foreach (var (_, policy, members) in groups)
        {
            var drift = members
                .SelectMany(i => perBinding[i]
                    .Where(f => PolicyDrift.Contains(f.Check) && f.Status is CheckStatus.Warn or CheckStatus.Fail)
                    .Select(f => $"{bindings[i].Spec.Name} {f.Check}"))
                .ToList();
            if (!trust && drift.Count == 0)
            {
                continue;
            }

            var checklist = policy with { PackageIds = [.. members.Select(i => bindings[i].Spec.Name)] };
            var why = drift.Count > 0 ? $"check it exists ({string.Join(", ", drift)} drifted)" : "confirm it by hand";
            perBinding[members[^1]].Add(new(
                $"nuget:{checklist.PackageGlob}", "policy", CheckStatus.Manual, $"no API to read the policy; {why}:\n{checklist.Render()}", NuGetGovernance.TrustUrl));
        }
    }

    // The policy a binding needs, minus its package IDs.
    private static PolicyChecklist Policy(PackageBinding binding)
    {
        var spec = binding.Spec;
        var slug = Slug(binding.Repo) ?? binding.Repo;
        var slash = slug.IndexOf('/', StringComparison.Ordinal);
        var (owner, name) = slash < 0 ? (slug, string.Empty) : (slug[..slash], slug[(slash + 1)..]);
        return new(spec.NuGetOwner, owner, name, spec.Workflow, spec.Environment, spec.Scopes, []);
    }

    [GeneratedRegex(@"^\s*id-token:\s*write\b", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex IdTokenWritePattern();

    [GeneratedRegex(@"secrets\.(NPM_TOKEN|NODE_AUTH_TOKEN|NUGET_API_KEY|NUGET_TOKEN)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TokenSecretPattern();

    [GeneratedRegex(@"cipr|\+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CiBuildPattern();

    private sealed class Report(string package)
    {
        public List<Finding> Findings { get; } = [];

        public void Add(string check, CheckStatus status, string detail, string fix = "") => Findings.Add(new(package, check, status, detail, fix));
    }
}
