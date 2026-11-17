using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig.Core.Config;

/// <summary>
/// <c>config/managed-projects.json</c>: the estate's project registry. The loader reads entries one at a time so a
/// single misshapen entry is skipped rather than losing the registry.
/// </summary>
public sealed record ManagedProjectsConfig
{
    /// <summary>The declared projects, in the file's order; a missing section is no projects.</summary>
    public IReadOnlyList<ManagedProjectEntry>? Projects { get; init; }
}

/// <summary>
/// One <c>projects</c> entry from <c>config/managed-projects.json</c>. Optional text the file omits arrives as null from
/// the source-generated reader, so <see cref="Workspace.ManagedProjects"/> applies the registry's empty-string defaults.
/// </summary>
public sealed record ManagedProjectEntry
{
    /// <summary>The clone's folder name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Its remote, such as <c>git@github.com:owner/name.git</c>.</summary>
    public string Repo { get; init; } = string.Empty;

    /// <summary>What it is.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Its domain, for the estate router table.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Its tech stack, for the estate router table.</summary>
    public string Stack { get; init; } = string.Empty;

    /// <summary>Its primary branch, when it is not the default.</summary>
    public string? Branch { get; init; }

    /// <summary>False for a retired project, whose clone is kept as an archive; an absent flag is enabled.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Enabled { get; init; }

    /// <summary>The Orca layout declaration, when the project has one.</summary>
    public ManagedProjectOrca? Orca { get; init; }
}

/// <summary>The <c>orca</c> block of a managed project.</summary>
public sealed record ManagedProjectOrca
{
    /// <summary>The Orca group the project is shown in.</summary>
    public string Group { get; init; } = string.Empty;
}

/// <summary>
/// <c>config/repo_policies.json</c>: the default policy layer, the named profiles and overrides, language presets and
/// the GitHub Apps the governance commands reconcile. Unknown keys are ignored.
/// </summary>
public sealed record RepoPoliciesConfig
{
    /// <summary>The schema URL, as checked in; read only so a roundtrip keeps it.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    /// <summary>The file's own name, as checked in.</summary>
    public string? Name { get; init; }

    /// <summary>The file's own version, as checked in.</summary>
    public string? Version { get; init; }

    /// <summary>The layer applied to every repo unless a profile or override declares the same key.</summary>
    public PolicyLayerConfig? Defaults { get; init; }

    /// <summary>Named layers, keyed by profile name.</summary>
    public IReadOnlyDictionary<string, PolicyProfileConfig>? Profiles { get; init; }

    /// <summary>A GitHub owner's profile, keyed by owner.</summary>
    public IReadOnlyDictionary<string, string>? OwnerProfiles { get; init; }

    /// <summary>Per-repo layers, keyed by bare name or <c>owner/name</c>.</summary>
    public IReadOnlyDictionary<string, PolicyLayerConfig>? RepoOverrides { get; init; }

    /// <summary>Language presets, keyed by the id a policy names.</summary>
    public IReadOnlyDictionary<string, LanguagePresetConfig>? LanguagePresets { get; init; }

    /// <summary>The declared GitHub Apps, keyed by slug.</summary>
    public IReadOnlyDictionary<string, GithubAppConfig>? GithubApps { get; init; }
}

/// <summary>A profile: a policy layer plus the repos it registers and the policy files it writes.</summary>
public sealed record PolicyProfileConfig : PolicyLayerConfig
{
    /// <summary>The repos it registers, by folder name.</summary>
    public IReadOnlyList<string>? Repos { get; init; }

    /// <summary>Its <c>policies</c> list, such as <c>ai_policy</c>.</summary>
    public IReadOnlyList<string>? Policies { get; init; }
}

/// <summary>One policy layer: the keys any of <c>defaults</c>, a profile or a repo override may declare.</summary>
public record PolicyLayerConfig
{
    /// <summary>The languages the repo is treated as using.</summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>The <c>.gitignore</c> entries a repo needs.</summary>
    public IReadOnlyList<GitignoreEntryConfig>? GitignoreEntries { get; init; }

    /// <summary>The AGENTS.md hygiene rules.</summary>
    public AgentsMdHygieneConfig? AgentsMdHygiene { get; init; }

    /// <summary>Which repos a governance sweep selects.</summary>
    public RepoSelectionConfig? RepoSelection { get; init; }

    /// <summary>The owners a repo may belong to.</summary>
    public IReadOnlyList<string>? AllowedOwners { get; init; }

    /// <summary>
    /// The expected owner. The raw element distinguishes absent (its value kind stays <c>Undefined</c>)
    /// from an explicit <c>null</c>, which declares that no owner is expected.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement ExpectedOwner { get; init; }

    /// <summary>The branch pull requests target.</summary>
    public string? ExpectedBaseBranch { get; init; }

    /// <summary>The merge and deletion settings applied to the repo.</summary>
    public RepoMergeSettingsConfig? RepoSettings { get; init; }

    /// <summary>The labels governance ensures exist.</summary>
    public IReadOnlyList<LabelConfig>? Labels { get; init; }

    /// <summary>Deployment environments, keyed by name.</summary>
    public IReadOnlyDictionary<string, EnvironmentConfig>? Environments { get; init; }

    /// <summary>The status checks a pull request must pass.</summary>
    public IReadOnlyList<string>? RequiredChecks { get; init; }

    /// <summary>GitHub Pages settings.</summary>
    public PagesConfig? Pages { get; init; }

    /// <summary>The upstream a fork tracks.</summary>
    public ForkConfig? Fork { get; init; }

    /// <summary>The copyright holder named in generated files.</summary>
    public string? CopyrightHolder { get; init; }

    /// <summary>The contact address named in generated files.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>The license, declared as a bare type or a <c>type</c> object.</summary>
    public LicenseConfig? License { get; init; }

    /// <summary>Which merged branches cleanup may delete.</summary>
    public BranchCleanupConfig? BranchCleanup { get; init; }

    /// <summary>The shell and PowerShell script ban.</summary>
    public ScriptBanConfig? ScriptBan { get; init; }

    /// <summary>The Vercel gating settings.</summary>
    public VercelGatingConfig? Vercel { get; init; }
}

/// <summary>A <c>.gitignore</c> entry a repo needs.</summary>
public sealed record GitignoreEntryConfig
{
    /// <summary>Its short name, such as <c>temp</c>.</summary>
    public string? Name { get; init; }

    /// <summary>The comment written above it.</summary>
    public string? Comment { get; init; }

    /// <summary>The lines it writes.</summary>
    public IReadOnlyList<string>? Write { get; init; }

    /// <summary>The existing lines that satisfy it; defaults to <see cref="Write"/>.</summary>
    public IReadOnlyList<string>? SatisfiedBy { get; init; }

    /// <summary>Whether any or all of <see cref="SatisfiedBy"/> must be present.</summary>
    public string? MatchMode { get; init; }
}

/// <summary>The AGENTS.md hygiene rules one layer declares.</summary>
public sealed record AgentsMdHygieneConfig
{
    /// <summary>The longest an AGENTS.md may be.</summary>
    public int? MaxLines { get; init; }

    /// <summary>How a CLAUDE.md beside an AGENTS.md is treated.</summary>
    public string? ClaudeMd { get; init; }

    /// <summary>Other agent instruction files a repo may not carry.</summary>
    public IReadOnlyList<string>? BespokeAgentFiles { get; init; }

    /// <summary>Files an AGENTS.md must name.</summary>
    public IReadOnlyList<string>? Require { get; init; }

    /// <summary>Whether referenced paths are checked to exist.</summary>
    public bool? CheckPaths { get; init; }

    /// <summary>The checks reported without failing.</summary>
    public IReadOnlyList<string>? ReportOnly { get; init; }

    /// <summary>How many entries the docs index must carry.</summary>
    public int? DocsIndexMinFiles { get; init; }

    /// <summary>The longest code block an AGENTS.md may contain.</summary>
    public int? MaxCodeBlockLines { get; init; }
}

/// <summary>Which repos a governance sweep selects.</summary>
public sealed record RepoSelectionConfig
{
    /// <summary>Whether forks are managed.</summary>
    public bool? ManageForks { get; init; }

    /// <summary>Whether a repo must grant push access to be managed.</summary>
    public bool? RequirePushAccess { get; init; }

    /// <summary>Whether the repo is exempt from a sweep.</summary>
    public bool? Exempt { get; init; }
}

/// <summary>The merge settings governance applies to a repo.</summary>
public sealed record RepoMergeSettingsConfig
{
    /// <summary>Whether a merged branch is deleted.</summary>
    public bool? DeleteBranchOnMerge { get; init; }

    /// <summary>Whether squash merges are allowed.</summary>
    public bool? AllowSquashMerge { get; init; }

    /// <summary>Whether merge commits are allowed.</summary>
    public bool? AllowMergeCommit { get; init; }

    /// <summary>Whether rebase merges are allowed.</summary>
    public bool? AllowRebaseMerge { get; init; }

    /// <summary>Whether auto-merge is allowed.</summary>
    public bool? AllowAutoMerge { get; init; }
}

/// <summary>A label governance ensures exists.</summary>
public sealed record LabelConfig
{
    /// <summary>Its name.</summary>
    public string? Name { get; init; }

    /// <summary>Its colour, as six hex digits.</summary>
    public string? Color { get; init; }

    /// <summary>Its description.</summary>
    public string? Description { get; init; }
}

/// <summary>A deployment environment and its approval gate.</summary>
public sealed record EnvironmentConfig
{
    /// <summary>How long a deployment waits before proceeding.</summary>
    public int? WaitTimer { get; init; }

    /// <summary>
    /// The required reviewers. A <see cref="JsonElement"/> so absent (undeclared) stays distinct from
    /// an explicit <c>null</c>, which is a mistake rather than a way of saying none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Reviewers { get; init; }
}

/// <summary>GitHub Pages settings.</summary>
public sealed record PagesConfig
{
    /// <summary>Whether Pages is published.</summary>
    public bool? Enabled { get; init; }

    /// <summary>How it is built: <c>workflow</c> or <c>legacy</c>.</summary>
    public string? BuildType { get; init; }

    /// <summary>The custom domain, when one is set.</summary>
    public string? Cname { get; init; }

    /// <summary>Whether HTTPS is enforced.</summary>
    public bool? HttpsEnforced { get; init; }
}

/// <summary>The upstream a fork tracks.</summary>
public sealed record ForkConfig
{
    /// <summary>The upstream repo.</summary>
    public string? Upstream { get; init; }

    /// <summary>The upstream branch; <c>main</c> when omitted.</summary>
    public string? UpstreamBranch { get; init; }
}

/// <summary>A license, declared as a bare type or as an object with a <c>type</c>.</summary>
[JsonConverter(typeof(LicenseConfigJsonConverter))]
public sealed record LicenseConfig
{
    /// <summary>The license type, such as <c>MIT</c>.</summary>
    public string? Type { get; init; }

    /// <summary>Whether the file declared it as a bare string, so a roundtrip writes the same form.</summary>
    [JsonIgnore]
    public bool Bare { get; init; }
}

/// <summary>Which merged branches cleanup may delete.</summary>
public sealed record BranchCleanupConfig
{
    /// <summary>Whether cleanup runs for the repo.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Branches never deleted.</summary>
    public IReadOnlyList<string>? Protected { get; init; }

    /// <summary>Whether a branch with an open PR is kept.</summary>
    public bool? ProtectOpenPrHeads { get; init; }

    /// <summary>Prefixes bots recreate their own branches under.</summary>
    public IReadOnlyList<string>? ProtectPrefixes { get; init; }
}

/// <summary>The estate-wide ban on shell and PowerShell scripts.</summary>
public sealed record ScriptBanConfig
{
    /// <summary>Whether the ban applies.</summary>
    public bool? Enabled { get; init; }

    /// <summary>The extensions banned anywhere in the repo.</summary>
    public IReadOnlyList<string>? BannedExtensions { get; init; }

    /// <summary>Paths exempt from the ban.</summary>
    public IReadOnlyList<string>? AllowedExceptions { get; init; }

    /// <summary>The script extensions allowed in <c>scripts/</c>.</summary>
    public IReadOnlyList<string>? AllowedScriptExtensions { get; init; }

    /// <summary>The script extensions banned in <c>scripts/</c>.</summary>
    public IReadOnlyList<string>? BannedScriptExtensions { get; init; }
}

/// <summary>The Vercel gating settings.</summary>
public sealed record VercelGatingConfig
{
    /// <summary>The Vercel team the projects belong to.</summary>
    public string? TeamId { get; init; }

    /// <summary>How far back deployments are read.</summary>
    public int? WindowDays { get; init; }
}

/// <summary>A configured language: the files that reveal it and the <c>.gitignore</c> entries it needs.</summary>
public sealed record LanguagePresetConfig
{
    /// <summary>Its display name, matched ignoring case; the preset's key when omitted.</summary>
    public string? Name { get; init; }

    /// <summary>Other names for it, lowercased.</summary>
    public IReadOnlyList<string>? Aliases { get; init; }

    /// <summary>File names or patterns whose presence reveals it.</summary>
    public IReadOnlyList<string>? DetectFiles { get; init; }

    /// <summary>The entries a repo in this language needs.</summary>
    public IReadOnlyList<GitignoreEntryConfig>? GitignoreEntries { get; init; }

    /// <summary>The script extensions it allows in <c>scripts/</c>.</summary>
    public IReadOnlyList<string>? ScriptExtensions { get; init; }

    /// <summary>The script extensions it bans.</summary>
    public IReadOnlyList<string>? BannedScriptExtensions { get; init; }
}

/// <summary>A declared GitHub App: <c>github_apps.{slug}</c>.</summary>
public sealed record GithubAppConfig
{
    /// <summary>Its display name; the slug when omitted.</summary>
    public string? Name { get; init; }

    /// <summary>Where a person installs it.</summary>
    [JsonPropertyName("install_url")]
    public string? InstallPage { get; init; }

    /// <summary>The in-repo config file it reads, if any.</summary>
    public string? ConfigTemplate { get; init; }

    /// <summary>Its enrolment on each account, by account name.</summary>
    public IReadOnlyDictionary<string, GithubAppAccountConfig>? Accounts { get; init; }
}

/// <summary>Which repos a GitHub App should reach on one account.</summary>
public sealed record GithubAppAccountConfig
{
    /// <summary>Whether its absence fails the audit.</summary>
    public bool? Required { get; init; }

    /// <summary>The repos it should reach, when not <see cref="AllRepos"/>.</summary>
    public IReadOnlyList<string>? Repos { get; init; }

    /// <summary>Whether it should reach every repo of the account.</summary>
    public bool? AllRepos { get; init; }

    /// <summary>Repos it must not reach; <c>exclude_repos</c> is the older name.</summary>
    public IReadOnlyList<string>? DisallowRepos { get; init; }

    /// <summary>Repos it must not reach, under the older key.</summary>
    public IReadOnlyList<string>? ExcludeRepos { get; init; }
}

/// <summary><c>config/cli-allowlists.json</c>: the command globs by tier and the skills allowlist.</summary>
public sealed record CliAllowlistsConfig
{
    /// <summary>The file's schema version, when declared.</summary>
    public int? Version { get; init; }

    /// <summary>The skills allowlist, inline or by file.</summary>
    public CliSkillsConfig? Skills { get; init; }

    /// <summary>The command rules, keyed by risk tier.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CliRuleConfig>>? Commands { get; init; }
}

/// <summary>The skills allowlist section.</summary>
public sealed record CliSkillsConfig
{
    /// <summary>The file beside the policy that lists the skills.</summary>
    public string? AllowlistFile { get; init; }

    /// <summary>The skills named inline.</summary>
    public IReadOnlyList<string>? Allow { get; init; }
}

/// <summary>
/// One command rule: a bare pattern or an object naming the pattern, its rationale and the systems it does not apply to.
/// </summary>
[JsonConverter(typeof(CliRuleConfigJsonConverter))]
public sealed record CliRuleConfig
{
    /// <summary>The command glob.</summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>Why the rule exists, for the audit trail.</summary>
    public string Rationale { get; init; } = string.Empty;

    /// <summary>The assistant systems the rule does not apply to.</summary>
    public IReadOnlyList<string> ExcludeSystems { get; init; } = [];

    /// <summary>Whether the file declared it as a bare pattern, so a roundtrip writes the same form.</summary>
    [JsonIgnore]
    public bool Bare { get; init; }
}

/// <summary>
/// <c>config/estate-scopes.json</c>: the GitHub accounts whose repositories are cloned into <c>personal/</c>, and the
/// orgs whose repositories sit in a sibling folder named for the org.
/// </summary>
public sealed record EstateScopesConfig
{
    /// <summary>The personal estate's scopes.</summary>
    public EstatePersonalScopesConfig? Personal { get; init; }
}

/// <summary>The <c>personal</c> scopes.</summary>
public sealed record EstatePersonalScopesConfig
{
    /// <summary>GitHub accounts whose repositories are cloned into <c>personal/</c>.</summary>
    public IReadOnlyList<string>? Owners { get; init; }

    /// <summary>GitHub orgs whose repositories sit in a sibling folder named for the org.</summary>
    public IReadOnlyList<string>? Orgs { get; init; }
}
