using System.Text.Json.Nodes;
using Rig.Core.Policy;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>Whether a checkout exists locally and links to a GitHub repo that exists.</summary>
/// <param name="Status"><c>EXISTS</c>, <c>MISSING_LOCAL</c>, <c>NOT_LINKED_TO_GITHUB</c> or <c>NOT_FOUND_ON_GITHUB</c>.</param>
/// <param name="Slug">The repo's <c>owner/name</c>, when one was found.</param>
public sealed record RepoExistence(string Status, string? Slug)
{
    /// <summary>Whether the checkout links to a live GitHub repo.</summary>
    public bool Exists => Status == "EXISTS";
}

/// <summary>Whether a repo's merge settings and default branch match the policy. An unreadable repo reads as the defaults, not aligned.</summary>
/// <param name="IsAligned">Whether nothing drifted.</param>
/// <param name="DeleteOnMerge">Whether branches are deleted on merge.</param>
/// <param name="SquashOnly">Whether squash is the only merge method allowed.</param>
/// <param name="DefaultBranch">The live default branch.</param>
/// <param name="ExpectedBranch">The branch the policy expects.</param>
/// <param name="Error">Why GitHub could not be read.</param>
public sealed record SettingsInspection(
    bool IsAligned, bool DeleteOnMerge = false, bool SquashOnly = false, string DefaultBranch = "main", string ExpectedBranch = "main", string? Error = null)
{
    /// <summary>Whether the default branch is the expected one.</summary>
    public bool BaseBranchAligned => DefaultBranch == ExpectedBranch;
}

/// <summary>Whether the managed ruleset is active as declared.</summary>
/// <param name="Status"><c>ACTIVE</c>, <c>DRIFTED: …</c>, <c>MISSING</c>, <c>N/A (Private Repo)</c> or <c>error</c>.</param>
/// <param name="IsAligned">Whether it needs no write; a private repo whose plan serves no rulesets counts as aligned.</param>
/// <param name="Id">The live ruleset's id, when there is one.</param>
/// <param name="Drift">How the live ruleset differs from the declared one.</param>
public sealed record RulesetInspection(string Status, bool IsAligned, long? Id, IReadOnlyList<string> Drift);

/// <summary>What one GitHub-side alignment step did.</summary>
/// <param name="Status">The step's verdict as the enforce table prints it.</param>
/// <param name="Drift">On a ruleset dry run, what the update would change.</param>
public sealed record AlignOutcome(string Status, IReadOnlyList<string> Drift);

/// <summary>Every convention a repo is held to, read without writing anything.</summary>
/// <param name="Name">The checkout's folder name.</param>
/// <param name="Slug">The repo's <c>owner/name</c>.</param>
/// <param name="Owner">Owner governance.</param>
/// <param name="Settings">Merge settings and default branch.</param>
/// <param name="Ruleset">The managed ruleset.</param>
/// <param name="Npmrc">The <c>.npmrc</c> check.</param>
/// <param name="Agents">The <c>AGENTS.md</c> check.</param>
/// <param name="Gitignore">The <c>.gitignore</c> check.</param>
/// <param name="Todo">The <c>TODO.md</c> check.</param>
/// <param name="Pinned">The workflow pinned-SHA check.</param>
/// <param name="LsLint">The <c>.ls-lint.yml</c> check.</param>
/// <param name="Names">Required check names against the workflows.</param>
/// <param name="Dependabot">The Dependabot / Renovate conflict check.</param>
public sealed record ConventionsReport(
    string Name,
    string Slug,
    OwnerInspection Owner,
    SettingsInspection Settings,
    RulesetInspection Ruleset,
    HygieneResult Npmrc,
    HygieneResult Agents,
    GitignoreInspection Gitignore,
    HygieneResult Todo,
    HygieneResult Pinned,
    HygieneResult LsLint,
    CheckNamesResult Names,
    HygieneResult Dependabot)
{
    /// <summary>Alignment as <c>status</c> judges it: check names are left to <c>check-names</c>.</summary>
    public bool StatusAligned =>
        Owner.IsAligned && Settings.IsAligned && Ruleset.IsAligned && Npmrc.IsAligned && Agents.IsAligned
        && Gitignore.IsAligned && Todo.IsAligned && Pinned.IsAligned && LsLint.IsAligned && Dependabot.IsAligned;

    /// <summary>Alignment as <c>audit</c> judges it, check names included.</summary>
    public bool AuditAligned => StatusAligned && Names.IsAligned;

    /// <summary>The advisory for a repo whose owner maps to a profile that does not name it yet.</summary>
    public string? NotAdopted => Owner.InferredProfile is { } profile ? Conventions.NotAdopted(profile) : null;

    /// <summary>
    /// The audit's lines for this repo: a verdict, the drift under it, then the advisories, which never count as drift.
    /// </summary>
    public IReadOnlyList<string> AuditLines()
    {
        var lines = new List<string>();
        if (AuditAligned)
        {
            lines.Add($"✓ {Name} ({Slug}): FULLY COMPLIANT");
        }
        else
        {
            lines.Add($"⚠ {Name} ({Slug}): DRIFT DETECTED");
            Detail(!Owner.IsAligned, $"Repository owner/org governance failure: {Owner.Message}");
            Detail(!Settings.BaseBranchAligned, $"Base branch deviation: default branch is '{Settings.DefaultBranch}', expected '{Settings.ExpectedBranch}'");
            if (!(Settings.DeleteOnMerge && Settings.SquashOnly))
            {
                lines.Add("   - Repo settings (squash merge / delete branch) not enforced");
            }
            else
            {
                Detail(!Settings.IsAligned && Settings.BaseBranchAligned, "Repo settings not enforced");
            }

            Detail(!Ruleset.IsAligned, $"GitHub Ruleset: {Ruleset.Status}");
            Detail(!Npmrc.IsAligned, $".npmrc status: {Npmrc.Status}");
            Detail(!Agents.IsAligned, $"AGENTS.md status: {Agents.Status}");
            Detail(!Gitignore.IsAligned, $".gitignore status: {Gitignore.Status}");
            Detail(!Todo.IsAligned, $"TODO.md status: {Todo.Status}");
            Detail(!Pinned.IsAligned, $"Workflow Pinned SHAs status: {Pinned.Status}");
            Detail(!LsLint.IsAligned, $".ls-lint.yml status: {LsLint.Status}");
            if (!Names.IsAligned)
            {
                lines.Add($"   - Required check(s) no pull_request job declares: {PyRepr.List(Names.Missing)}");
                lines.Add($"     declared: {PyRepr.List(Names.Declared.Keys.Order(StringComparer.Ordinal))} — run 'repo-governance check-names' for detail");
            }

            Detail(!Dependabot.IsAligned, $"Dependabot / Renovate conflict: {Dependabot.Reason}");
        }

        if (NotAdopted is { } advisory)
        {
            lines.Add($"   · {advisory}");
        }

        // Shape is advisory: a compliant repo can still carry a name that breaks the next time CI is touched.
        lines.AddRange(Names.Warnings.Select(pair => $"   · check name {PyRepr.Str(pair.Key)}: {string.Join("; ", pair.Value)}"));
        return lines;

        void Detail(bool drifted, string line)
        {
            if (drifted)
            {
                lines.Add($"   - {line}");
            }
        }
    }

    /// <summary>The cells of this repo's row in the status table.</summary>
    public IReadOnlyList<string> StatusRow() =>
    [
        Name,
        Slug,
        Owner.IsAligned ? $"✓ {Owner.ActualOwner}" : $"✗ {Owner.ActualOwner ?? "N/A"}",
        Settings.BaseBranchAligned ? $"✓ {Settings.DefaultBranch}" : $"✗ {Settings.DefaultBranch} (need {Settings.ExpectedBranch})",
        Settings.IsAligned ? "SQUASH+DELETE+AUTOMERGE" : "DRIFTED",
        Ruleset.Status.Contains("N/A", StringComparison.Ordinal) || !Ruleset.IsAligned ? Ruleset.Status : "ACTIVE (+THREADS RESOLVED)",
        Npmrc.Status,
        Agents.Status,
        Pinned.Status,
        LsLint.Status,
        StatusAligned ? "✓ ALIGNED" : "⚠ DRIFTED",
    ];
}

/// <summary>
/// The per-repo conventions behind <c>status</c>, <c>audit</c> and <c>enforce</c>: owner, merge settings, the managed
/// ruleset and the hygiene files. Ported from <c>rig.repo_governance.cli_repo_checks</c> and the inspect and align
/// halves of <c>github_rulesets</c>.
/// </summary>
/// <param name="proc">Runs gh.</param>
/// <param name="api">GitHub's REST API.</param>
/// <param name="repos">Resolves a checkout's slug.</param>
/// <param name="gitignores">The <c>.gitignore</c> check and the policy key.</param>
/// <param name="policies">The policy.</param>
public sealed class Conventions(IProc proc, IGitHubApi api, GovernedRepos repos, Gitignores gitignores, RepoPolicies policies)
{
    private const string NotServed = "N/A (Private Repo)";

    private const string Unserved = "Upgrade to GitHub Pro or make this repository public";

    private const string PrivateSkip = "skipped (private repo restriction)";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The advisory for a repo whose owner maps to <paramref name="profile"/> but which no profile names.</summary>
    public static string NotAdopted(string profile) => $"not adopted: add it to profile {profile} in config/repo_policies.json";

    /// <summary>Whether <paramref name="repoPath"/> is a checkout whose GitHub repo exists.</summary>
    public async Task<RepoExistence> ExistsAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        if (!Path.Exists(Path.Join(repoPath, ".git")))
        {
            return new("MISSING_LOCAL", null);
        }

        if (await repos.GhRepoNameAsync(repoPath, cancellationToken).ConfigureAwait(false) is not { Length: > 0 } slug)
        {
            return new("NOT_LINKED_TO_GITHUB", null);
        }

        var view = await proc.RunAsync(
            new ProcRequest(["gh", "repo", "view", slug, "--json", "nameWithOwner", "-q", ".nameWithOwner"]) { WorkingDirectory = repoPath, Timeout = Timeout },
            cancellationToken).ConfigureAwait(false);
        return view.Ok ? new("EXISTS", view.Out.Trim()) : new("NOT_FOUND_ON_GITHUB", slug);
    }

    /// <summary>Every convention <paramref name="repoPath"/>, whose GitHub repo is <paramref name="slug"/>, is held to.</summary>
    public async Task<ConventionsReport> InspectAsync(string repoPath, string slug, CancellationToken cancellationToken = default)
    {
        var key = await gitignores.PolicyKeyAsync(repoPath, cancellationToken).ConfigureAwait(false);
        return new(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath)),
            slug,
            RepoOwners.Inspect(policies, slug),
            await InspectSettingsAsync(slug, cancellationToken).ConfigureAwait(false),
            await InspectRulesetAsync(slug, cancellationToken).ConfigureAwait(false),
            RepoHygiene.InspectNpmrc(repoPath),
            RepoHygiene.InspectAgentsMd(repoPath),
            await gitignores.InspectAsync(repoPath, cancellationToken: cancellationToken).ConfigureAwait(false),
            RepoHygiene.InspectTodoMd(repoPath),
            RepoHygiene.InspectPinnedShas(repoPath),
            RepoHygiene.InspectLsLint(repoPath, policies, key),
            CheckNames.Inspect(repoPath, slug, policies),
            RepoHygiene.InspectDependabotRenovate(repoPath));
    }

    /// <summary>The repo's merge settings and default branch against the policy.</summary>
    public async Task<SettingsInspection> InspectSettingsAsync(string slug, CancellationToken cancellationToken = default)
    {
        var resource = new RepoSettingsResource();
        var target = new Target(string.Empty, slug);
        var desired = resource.Desired(policies, target)!;
        LiveRepoSettings live;
        try
        {
            live = await resource.ReadAsync(api, target, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubReadException e)
        {
            return new(false, Error: e.Message);
        }

        return new(
            RepoSettingsResource.Drift(desired, live).Count == 0,
            live.DeleteBranchOnMerge,
            live.AllowSquashMerge == true && live.AllowMergeCommit == false && live.AllowRebaseMerge == false,
            live.DefaultBranch is { Length: > 0 } branch ? branch : "main",
            desired.DefaultBranch);
    }

    /// <summary>The managed ruleset against the declared one.</summary>
    public async Task<RulesetInspection> InspectRulesetAsync(string slug, CancellationToken cancellationToken = default)
    {
        LiveRuleset live;
        try
        {
            live = await RulesetResource.ReadRulesetAsync(api, slug, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubReadException)
        {
            return new("error", false, null, []);
        }

        if (!live.Served)
        {
            return new(NotServed, true, null, []);
        }

        if (live.Rules is null)
        {
            return new("MISSING", false, null, []);
        }

        var drift = RulesetShape.From(JsonNode.Parse(Payload(slug)))!.DriftOf(live.Rules);
        return new(drift.Count > 0 ? $"DRIFTED: {string.Join("; ", drift)}" : "ACTIVE", drift.Count == 0, live.Id, drift);
    }

    /// <summary>Sets the policy's merge settings and default branch with one <c>PATCH repos/&lt;repo&gt;</c>, unconditionally.</summary>
    public async Task<AlignOutcome> AlignSettingsAsync(string slug, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (dryRun)
        {
            return new("dry_run", []);
        }

        var desired = new RepoSettingsResource().Desired(policies, new Target(string.Empty, slug))!;
        var ran = await GitHubCalls.SendAsync(api, "PATCH", $"repos/{slug}", desired.Body(), cancellationToken).ConfigureAwait(false);
        return new(ran.Ok ? "success" : "error", []);
    }

    /// <summary>Creates the managed ruleset, or replaces it in place where it drifted, so it is never duplicated.</summary>
    public async Task<AlignOutcome> AlignRulesetAsync(string slug, bool dryRun, CancellationToken cancellationToken = default)
    {
        var existing = await InspectRulesetAsync(slug, cancellationToken).ConfigureAwait(false);
        if (existing.Status == NotServed)
        {
            return new(PrivateSkip, []);
        }

        if (existing.Status == "error")
        {
            return new("error", []);
        }

        if (existing.IsAligned)
        {
            return new("already_active", []);
        }

        if (dryRun)
        {
            return new("dry_run", existing.Drift);
        }

        var ran = existing.Id is { } id
            ? await GitHubCalls.SendAsync(api, "PUT", $"repos/{slug}/rulesets/{id}", Payload(slug), cancellationToken).ConfigureAwait(false)
            : await GitHubCalls.SendAsync(api, "POST", $"repos/{slug}/rulesets", Payload(slug), cancellationToken).ConfigureAwait(false);
        return ran.Ok ? new("success", [])
            : ran.Err.Contains(Unserved, StringComparison.Ordinal) ? new(PrivateSkip, [])
            : new("error", []);
    }

    // The declared ruleset as Python's json.dumps writes it; read back from the text, as the live one is.
    private string Payload(string slug) => PyJson.DumpsLine(RulesetResource.Payload(policies, slug));
}
