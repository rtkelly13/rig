using System.Text.Json;
using Rig.Core.Config;
using Rig.Core.Policy;

namespace Rig.Core.Governance;

/// <summary>A declared profile: the repos it registers and the policy files <c>sync-policies</c> writes to them.</summary>
/// <param name="Name">Its key under <c>profiles</c>.</param>
/// <param name="Repos">The repos it registers, by folder name.</param>
/// <param name="Policies">Its <c>policies</c> list, such as <c>ai_policy</c>.</param>
public sealed record PolicyProfile(string Name, IReadOnlyList<string> Repos, IReadOnlyList<string> Policies);

/// <summary>A configured language: the files that reveal it and the <c>.gitignore</c> entries it needs.</summary>
/// <param name="Id">The key it sits under in <c>language_presets</c>.</param>
/// <param name="Name">Its display name, matched ignoring case.</param>
/// <param name="Aliases">Other names for it, lowercased.</param>
/// <param name="DetectFiles">File names or <c>fnmatch</c> patterns whose presence reveals it.</param>
/// <param name="GitignoreEntries">The entries a repo in this language needs.</param>
public sealed record LanguagePreset(
    string Id,
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> DetectFiles,
    IReadOnlyList<GitignoreEntry> GitignoreEntries)
{
    /// <summary>The script extensions this language allows in <c>scripts/</c>.</summary>
    public IReadOnlyList<string> ScriptExtensions { get; init; } = [];

    /// <summary>The script extensions this language bans.</summary>
    public IReadOnlyList<string> BannedScriptExtensions { get; init; } = [];

    /// <summary>Whether <paramref name="fileName"/> is one of <see cref="DetectFiles"/>.</summary>
    public bool MatchesFile(string fileName) =>
        DetectFiles.Any(pattern => pattern == fileName || Glob.Matches(fileName, pattern, OperatingSystem.IsWindows()));
}

/// <summary>
/// <c>config/repo_policies.json</c>, as far as the <c>.gitignore</c> policy, repo selection and the AGENTS.md audit read it. A key resolves from the repo's
/// override, then the profile naming it, then <c>defaults</c>, taking the first that declares it (no deep merge).
/// </summary>
public sealed class RepoPolicies
{
    /// <summary>The file's name under a rig checkout's <c>config/</c>.</summary>
    public const string FileName = "repo_policies.json";

    // Folder names (by prefix) whose children are not looked at when detecting a split repo's languages.
    private static readonly string[] SkippedDirPrefixes = [".", "node_modules", "temp", "vendor", "dist", "bin", "obj"];

    private readonly Layer _defaults;
    private readonly List<(string Name, Layer Layer, IReadOnlyList<string> Repos)> _profiles;
    private readonly List<(string Name, Layer Layer)> _overrides;
    private readonly List<(string Owner, string Profile)> _ownerProfiles;

    private RepoPolicies(
        Layer defaults,
        List<(string, Layer, IReadOnlyList<string>)> profiles,
        List<(string, Layer)> overrides,
        IReadOnlyList<LanguagePreset> presets,
        List<(string, string)> ownerProfiles,
        IReadOnlyList<AppPolicy> apps)
    {
        _defaults = defaults;
        Apps = apps;
        _profiles = profiles;
        _overrides = overrides;
        _ownerProfiles = ownerProfiles;
        Presets = presets;
    }

    /// <summary>A policy that requires nothing.</summary>
    public static RepoPolicies Empty { get; } = new(Layer.None, [], [], [], [], []);

    /// <summary>The declared GitHub Apps (<c>github_apps</c>), in the file's order.</summary>
    public IReadOnlyList<AppPolicy> Apps { get; }

    /// <summary>The language presets, in the file's order.</summary>
    public IReadOnlyList<LanguagePreset> Presets { get; }

    /// <summary>The declared <c>profiles</c>, in the file's order.</summary>
    public IReadOnlyList<PolicyProfile> Profiles { get; private init; } = [];

    /// <summary>
    /// The policy in <paramref name="path"/>. A missing or unparseable file requires nothing; a file of the wrong
    /// shape throws <see cref="InvalidDataException"/>, since falling back would silently drop protections.
    /// </summary>
    public static RepoPolicies Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        RepoPoliciesConfig file;
        try
        {
            file = ConfigLoader.Load(path, EstateConfigJsonContext.Default.RepoPoliciesConfig);
        }
        catch (ConfigLoadException e) when (e.Kind is ConfigLoadError.MissingFile or ConfigLoadError.InvalidJson)
        {
            return Empty;
        }
        catch (ConfigLoadException e)
        {
            throw new InvalidDataException(e.Message, e);
        }

        return Parse(file, path);
    }

    /// <summary>The policy in <paramref name="json"/>; <paramref name="source"/> names it in errors.</summary>
    public static RepoPolicies Parse(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        RepoPoliciesConfig file;
        try
        {
            file = ConfigLoader.Parse(json, source, EstateConfigJsonContext.Default.RepoPoliciesConfig);
        }
        catch (ConfigLoadException e)
        {
            throw new InvalidDataException(e.Message, e);
        }

        return Parse(file, source);
    }

    /// <summary>The policy in a parsed document, for a caller that already holds one; it is serialized back for the typed reader.</summary>
    public static RepoPolicies Parse(System.Text.Json.Nodes.JsonNode? root, string source) =>
        Parse(root?.ToJsonString() ?? "null", source);

    /// <summary>The policy in a decoded document; <paramref name="source"/> names it in errors.</summary>
    public static RepoPolicies Parse(RepoPoliciesConfig file, string source)
    {
        ArgumentNullException.ThrowIfNull(file);
        var profileConfigs = Map(file.Profiles).ToList();
        var profiles = profileConfigs
            .Select(pair => (pair.Key, Layer.From(pair.Value, source), (IReadOnlyList<string>)(pair.Value.Repos ?? [])))
            .ToList();
        var declared = profiles.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var owners = Map(file.OwnerProfiles).Select(pair => (Owner: pair.Key, Profile: pair.Value)).ToList();
        var unknown = owners
            .Where(pair => !declared.Contains(pair.Profile))
            .OrderBy(pair => pair.Owner, StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            throw Bad(source, "owner_profiles names undeclared profile(s): " + string.Join(", ", unknown.Select(pair => $"{pair.Owner} -> {pair.Profile}")));
        }

        return new(
            file.Defaults is { } defaults ? Layer.From(defaults, source) : Layer.None,
            profiles,
            [.. Map(file.RepoOverrides).Select(pair => (pair.Key, Layer.From(pair.Value, source)))],
            [.. Map(file.LanguagePresets).Select(pair => Preset(pair.Key, pair.Value, source))],
            owners,
            [.. Map(file.GithubApps).Select(pair => App(pair.Key, pair.Value, source))])
        {
            Profiles = [.. profileConfigs.Select(pair => new PolicyProfile(pair.Key, pair.Value.Repos ?? [], pair.Value.Policies ?? []))],
        };
    }

    // A missing section and an empty one both mean no entries.
    private static IEnumerable<KeyValuePair<TKey, TValue>> Map<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? section)
        where TKey : notnull => section ?? new Dictionary<TKey, TValue>();

    /// <summary>The apps declared for <paramref name="account"/>, matched ignoring case.</summary>
    public IReadOnlyList<AppPolicy> AppsForAccount(string account) =>
        [.. Apps.Where(app => app.Accounts.Keys.Any(name => string.Equals(name, account, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>Whether <paramref name="repo"/> (bare or <c>owner/name</c>) is in <paramref name="slug"/>'s <c>disallow_repos</c> on <paramref name="account"/>.</summary>
    public bool IsAppDisallowed(string repo, string account, string slug)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var bare = repo[(repo.LastIndexOf('/') + 1)..];
        return Apps.FirstOrDefault(app => app.Slug == slug)?.Accounts.GetValueOrDefault(account) is { } policy && policy.DisallowRepos.Contains(bare);
    }

    /// <summary>A preset's id for a language name, alias or id, ignoring case; an unknown one comes back trimmed and lowercased.</summary>
    public string NormalizeLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var cleaned = language.Trim().ToLowerInvariant();
        if (Presets.Any(preset => preset.Id == cleaned))
        {
            return cleaned;
        }

        return Presets.FirstOrDefault(preset => string.Equals(cleaned, preset.Name, StringComparison.OrdinalIgnoreCase) || preset.Aliases.Contains(cleaned))?.Id ?? cleaned;
    }

    /// <summary>
    /// The languages of the repo keyed <paramref name="key"/>: <paramref name="explicitLanguages"/> when given (each may
    /// list several, comma-separated), else its policy's <c>languages</c>, else those detected in <paramref name="repoPath"/>.
    /// </summary>
    public IReadOnlyList<string> ResolveLanguages(string key, string? repoPath = null, IReadOnlyList<string>? explicitLanguages = null)
    {
        if (explicitLanguages is { Count: > 0 })
        {
            var resolved = new List<string>();
            foreach (var language in explicitLanguages.SelectMany(language => language.Split(',')).Select(NormalizeLanguage))
            {
                if (language.Length > 0 && !resolved.Contains(language))
                {
                    resolved.Add(language);
                }
            }

            return resolved;
        }

        if (Resolve(key, layer => layer.Languages) is { Count: > 0 } configured)
        {
            return [.. configured.Select(NormalizeLanguage)];
        }

        return repoPath is not null && Directory.Exists(repoPath) ? DetectLanguages(repoPath) : [];
    }

    /// <summary>
    /// The presets whose files sit in <paramref name="repoPath"/> or one folder down (a split repo's <c>backend/</c>),
    /// in the file's order.
    /// </summary>
    public IReadOnlyList<string> DetectLanguages(string repoPath)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        if (!Directory.Exists(repoPath) || Presets.Count == 0)
        {
            return [];
        }

        var candidates = new List<string>();
        try
        {
            var children = Directory.EnumerateFileSystemEntries(repoPath).ToList();
            candidates.AddRange(children.Select(Path.GetFileName).OfType<string>().Where(name => !name.StartsWith(".git", StringComparison.Ordinal)));
            foreach (var child in children.Where(child => Directory.Exists(child) && !SkippedDirPrefixes.Any(Path.GetFileName(child).StartsWith)))
            {
                try
                {
                    candidates.AddRange(Directory.EnumerateFileSystemEntries(child).Select(Path.GetFileName).OfType<string>());
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // An unreadable folder reveals nothing.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return [.. Presets.Where(preset => candidates.Any(preset.MatchesFile)).Select(preset => preset.Id)];
    }

    /// <summary>
    /// The <c>.gitignore</c> entries the repo keyed <paramref name="key"/> needs: its policy's own, then each resolved
    /// language's that it has not already named.
    /// </summary>
    public IReadOnlyList<GitignoreEntry> GitignoreEntries(string key, string? repoPath = null, IReadOnlyList<string>? languages = null)
    {
        var entries = (Resolve(key, layer => layer.GitignoreEntries) ?? []).ToList();
        var seen = entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var language in ResolveLanguages(key, repoPath, languages))
        {
            var preset = Presets.FirstOrDefault(p => p.Id == language);
            foreach (var entry in preset?.GitignoreEntries ?? [])
            {
                if (seen.Add(entry.Name))
                {
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    /// <summary>The bespoke agent files the repo keyed <paramref name="key"/> must not carry (<c>agents_md_hygiene.bespoke_agent_files</c>).</summary>
    public IReadOnlyList<string> BespokeAgentFilesFor(string key) => AgentsMd(key).BespokeAgentFiles;

    /// <summary>The <c>AGENTS.md</c> hygiene rules for the repo keyed <paramref name="key"/>.</summary>
    public AgentsMdRules AgentsMd(string key) => Resolve(key, layer => layer.AgentsMd) ?? AgentsMdRules.Default;

    /// <summary>The selection rules for the repo keyed <paramref name="key"/>.</summary>
    public RepoSelectionRules SelectionRules(string key) => Resolve(key, layer => layer.RepoSelection) ?? RepoSelectionRules.Default;

    /// <summary>Whether a profile names the repo keyed <paramref name="key"/>: an explicit opt-in.</summary>
    public bool IsRegistered(string key)
    {
        var bare = BareName(key);
        return _profiles.Any(p => p.Repos.Any(repo => BareName(repo) == bare));
    }

    /// <summary>The GitHub owners the repo keyed <paramref name="key"/> may live under.</summary>
    public IReadOnlyList<string> AllowedOwners(string key) => Resolve(key, layer => layer.AllowedOwners, identity: true) ?? ["rtkelly13", "rtkelly-labs"];

    /// <summary>The owner the repo keyed <paramref name="key"/> is expected under, or null when none is configured.</summary>
    public string? ExpectedOwner(string key) => Resolve(key, layer => layer.ExpectedOwner, identity: true) is { Length: > 0 } owner ? owner : null;

    /// <summary>Who the repo keyed <paramref name="key"/>'s LICENSE names (<c>copyright_holder</c>), else <c>Ryan Kelly</c>.</summary>
    public string CopyrightHolder(string key) => Resolve(key, layer => layer.CopyrightHolder, identity: true) is { Length: > 0 } holder ? holder : "Ryan Kelly";

    /// <summary>The address the repo keyed <paramref name="key"/>'s community files give (<c>contact_email</c>), else <c>ryan@ryankelly.dev</c>.</summary>
    public string ContactEmail(string key) => Resolve(key, layer => layer.ContactEmail, identity: true) is { Length: > 0 } email ? email : "ryan@ryankelly.dev";

    /// <summary>The licence the repo keyed <paramref name="key"/> should carry (<c>license</c>, a string or <c>{"type": …}</c>), else <c>MIT</c>.</summary>
    public string LicenseType(string key) => Resolve(key, layer => layer.License, identity: true) is { Length: > 0 } license ? license : "MIT";

    /// <summary>The default branch the repo keyed <paramref name="key"/> should have (<c>expected_base_branch</c>), else <c>main</c>.</summary>
    public string ExpectedBaseBranch(string key) => Resolve(key, layer => layer.ExpectedBaseBranch) is { Length: > 0 } branch ? branch : "main";

    /// <summary>The merge settings the repo keyed <paramref name="key"/> should carry (<c>repo_settings</c>).</summary>
    public MergeSettings RepoSettings(string key) => Resolve(key, layer => layer.RepoSettings) ?? MergeSettings.Default;

    /// <summary>The label taxonomy of the repo keyed <paramref name="key"/>; empty means labels are not governed.</summary>
    public IReadOnlyList<LabelSpec> Labels(string key) => Resolve(key, layer => layer.Labels) ?? [];

    /// <summary>The deployment environments the repo keyed <paramref name="key"/> declares, in the file's order; most declare none.</summary>
    public IReadOnlyList<EnvironmentPolicy> Environments(string key) => Resolve(key, layer => layer.Environments) ?? [];

    /// <summary>The required check names of the repo keyed <paramref name="key"/>; empty when none are registered.</summary>
    public IReadOnlyList<string> RequiredChecks(string key) => Resolve(key, layer => layer.RequiredChecks) ?? [];

    /// <summary>The GitHub Pages policy of the repo keyed <paramref name="key"/>.</summary>
    public PagesPolicy Pages(string key) => Resolve(key, layer => layer.Pages) ?? PagesPolicy.Default;

    /// <summary>The upstream the repo keyed <paramref name="key"/> tracks, or null when it is not a managed fork.</summary>
    public ForkSpec? Fork(string key) => Resolve(key, layer => layer.Fork);

    /// <summary>Which remote branches cleanup may delete (<c>branch_cleanup</c>).</summary>
    public BranchCleanupRules BranchCleanup(string key) => Resolve(key, layer => layer.BranchCleanup) ?? BranchCleanupRules.Default;

    /// <summary>The script ban and its exceptions (<c>script_ban</c>).</summary>
    public ScriptBanRules ScriptBan(string key) => Resolve(key, layer => layer.ScriptBan) ?? ScriptBanRules.Default;

    /// <summary>The Vercel account facts the deployment-creation audit reads (<c>vercel</c>).</summary>
    public VercelGatingRules VercelGating(string key) => Resolve(key, layer => layer.Vercel) ?? VercelGatingRules.Default;

    /// <summary>
    /// The script extensions a repo allows and bans: the <c>script_ban</c> lists when it declares any, otherwise the
    /// union of its languages' presets, in order, with <c>.py</c> never banned in a Python repo.
    /// </summary>
    public RepoScriptRules ScriptRules(string key, string? repoPath = null)
    {
        var ban = ScriptBan(key);
        if (ban.AllowedScriptExtensions.Count > 0 || ban.BannedScriptExtensions.Count > 0)
        {
            return new(ban.AllowedScriptExtensions, ban.BannedScriptExtensions);
        }

        var allowed = new List<string>();
        var banned = new List<string>();
        var hasPython = false;
        foreach (var language in ResolveLanguages(key, repoPath))
        {
            var id = NormalizeLanguage(language);
            hasPython |= id == "python";
            if (Presets.FirstOrDefault(preset => preset.Id == id) is not { } preset)
            {
                continue;
            }

            allowed.AddRange(preset.ScriptExtensions.Where(ext => !allowed.Contains(ext)).Distinct());
            banned.AddRange(preset.BannedScriptExtensions.Where(ext => !banned.Contains(ext)).Distinct());
        }

        if (hasPython)
        {
            _ = banned.Remove(".py");
        }

        return new(allowed, banned);
    }

    /// <summary>
    /// The profile an unregistered <c>owner/name</c> <paramref name="key"/> falls back to through <c>owner_profiles</c>, or
    /// null: a bare name carries no owner, and a repo a profile names never falls back.
    /// </summary>
    public string? OwnerProfileFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || IsRegistered(key))
        {
            return null;
        }

        var owner = key[..slash];
        return _ownerProfiles.FirstOrDefault(pair => string.Equals(pair.Owner, owner, StringComparison.OrdinalIgnoreCase)).Profile;
    }

    /// <summary>The repo names registered under <paramref name="org"/>: those of profiles and overrides expecting it as owner.</summary>
    public IReadOnlyList<string> ReposForOrg(string org)
    {
        ArgumentNullException.ThrowIfNull(org);
        var repos = new List<string>();
        foreach (var (_, layer, names) in _profiles)
        {
            if (layer.ExpectedOwner is { Length: > 0 } owner && string.Equals(owner, org, StringComparison.OrdinalIgnoreCase))
            {
                repos.AddRange(names);
            }
        }

        foreach (var (name, layer) in _overrides)
        {
            if (layer.ExpectedOwner is { Length: > 0 } owner && string.Equals(owner, org, StringComparison.OrdinalIgnoreCase) && !repos.Contains(name))
            {
                repos.Add(name);
            }
        }

        return repos;
    }

    /// <summary>Whether <paramref name="repo"/> belongs to <paramref name="org"/>: by its slug's owner, its expected owner or registration.</summary>
    public bool IsRepoInOrg(string repo, string org)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var slash = repo.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0 && string.Equals(repo[..slash], org, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var bare = repo[(repo.LastIndexOf('/') + 1)..];
        if (ExpectedOwner(bare) is { } expected && string.Equals(expected, org, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var wanted = BareName(bare);
        return ReposForOrg(org).Any(name => BareName(name) == wanted);
    }

    // The first layer that declares the key: the repo's override, the profile naming it, then defaults. An unregistered
    // `owner/name` key whose owner has an owner_profiles entry takes that profile in the middle, but only for the
    // identity keys (who owns it); the others never come from an inferred profile.
    private T? Resolve<T>(string key, Func<Layer, T?> pick, bool identity = false)
        where T : class
    {
        var bare = BareName(key);
        var profile = _profiles.Where(p => p.Repos.Any(repo => BareName(repo) == bare)).Select(p => p.Layer).FirstOrDefault();
        if (profile is null && identity)
        {
            profile = OwnerProfile(key);
        }

        IEnumerable<Layer> layers =
        [
            .. _overrides.Where(o => BareName(o.Name) == bare).Take(1).Select(o => o.Layer),
            .. profile is null ? [] : new[] { profile },
            _defaults,
        ];
        return layers.Select(pick).FirstOrDefault(value => value is not null);
    }

    private Layer? OwnerProfile(string key)
    {
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return null;
        }

        var owner = key[..slash];
        var name = _ownerProfiles.FirstOrDefault(pair => string.Equals(pair.Owner, owner, StringComparison.OrdinalIgnoreCase)).Profile;
        return name is null ? null : _profiles.First(p => p.Name == name).Layer;
    }

    /// <summary>A key's last segment, lowercased: the file lists bare names, and GitHub names ignore case.</summary>
    public static string BareName(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key[(key.LastIndexOf('/') + 1)..].ToLowerInvariant();
    }

    private static LanguagePreset Preset(string id, LanguagePresetConfig spec, string source)
    {
        var name = spec.Name ?? string.Empty;
        return new(
            id,
            name.Length > 0 ? name : id,
            [.. (spec.Aliases ?? []).Select(alias => alias.ToLowerInvariant())],
            spec.DetectFiles ?? [],
            Entries(spec.GitignoreEntries, source) ?? [])
        {
            ScriptExtensions = spec.ScriptExtensions ?? [],
            BannedScriptExtensions = spec.BannedScriptExtensions ?? [],
        };
    }

    private static List<GitignoreEntry>? Entries(IReadOnlyList<GitignoreEntryConfig>? entries, string source) =>
        entries is null ? null : [.. entries.Select(entry => Entry(entry, source))];

    private static GitignoreEntry Entry(GitignoreEntryConfig spec, string source)
    {
        var write = spec.Write ?? [];
        var name = spec.Name ?? string.Empty;
        var mode = spec.MatchMode ?? "any";
        if (mode is not ("any" or "all"))
        {
            throw Bad(source, $"match_mode '{mode}' is neither 'any' nor 'all'");
        }

        return new(
            name.Length > 0 ? name : write.Count > 0 ? write[0] : "entry",
            spec.Comment ?? string.Empty,
            write,
            spec.SatisfiedBy ?? write,
            mode == "all");
    }

    private static AppPolicy App(string slug, GithubAppConfig spec, string source)
    {
        var accounts = new Dictionary<string, AppAccountPolicy>(StringComparer.Ordinal);
        foreach (var (account, value) in Map(spec.Accounts))
        {
            var disallow = value.DisallowRepos is { Count: > 0 } named ? named : value.ExcludeRepos ?? [];
            accounts[account] = new(value.Required ?? false, value.Repos ?? [], value.AllRepos ?? false, disallow);
        }

        return new(
            slug,
            spec.Name is { Length: > 0 } name ? name : slug,
            spec.InstallPage is { Length: > 0 } url ? url : $"https://github.com/apps/{slug}/installations/new",
            spec.ConfigTemplate is { Length: > 0 } template ? template : null,
            accounts);
    }

    private static InvalidDataException Bad(string source, string why) => new($"{source}: {why}");

    // agents_md_hygiene, whole: a field it leaves out takes the built-in default.
    private static AgentsMdRules? Hygiene(AgentsMdHygieneConfig? hygiene, string source)
    {
        if (hygiene is null)
        {
            return null;
        }

        var defaults = AgentsMdRules.Default;
        var claudeMd = hygiene.ClaudeMd ?? defaults.ClaudeMd;
        if (claudeMd is not ("symlink" or "absent" or "any"))
        {
            throw Bad(source, $"claude_md '{claudeMd}' is not one of symlink, absent, any");
        }

        return new()
        {
            MaxLines = hygiene.MaxLines ?? defaults.MaxLines,
            ClaudeMd = claudeMd,
            BespokeAgentFiles = hygiene.BespokeAgentFiles ?? defaults.BespokeAgentFiles,
            Require = hygiene.Require ?? defaults.Require,
            CheckPaths = hygiene.CheckPaths ?? defaults.CheckPaths,
            ReportOnly = hygiene.ReportOnly ?? defaults.ReportOnly,
            DocsIndexMinFiles = hygiene.DocsIndexMinFiles ?? defaults.DocsIndexMinFiles,
            MaxCodeBlockLines = hygiene.MaxCodeBlockLines ?? defaults.MaxCodeBlockLines,
        };
    }

    private static RepoSelectionRules? Selection(RepoSelectionConfig? selection)
    {
        if (selection is null)
        {
            return null;
        }

        var defaults = RepoSelectionRules.Default;
        return new(
            selection.ManageForks ?? defaults.ManageForks,
            selection.RequirePushAccess ?? defaults.RequirePushAccess,
            selection.Exempt ?? defaults.Exempt);
    }

    private static MergeSettings? Merge(RepoMergeSettingsConfig? block)
    {
        if (block is null)
        {
            return null;
        }

        var defaults = MergeSettings.Default;
        return new(
            block.DeleteBranchOnMerge ?? defaults.DeleteBranchOnMerge,
            block.AllowSquashMerge ?? defaults.AllowSquashMerge,
            block.AllowMergeCommit ?? defaults.AllowMergeCommit,
            block.AllowRebaseMerge ?? defaults.AllowRebaseMerge,
            block.AllowAutoMerge ?? defaults.AllowAutoMerge);
    }

    private static List<LabelSpec>? LabelsOf(IReadOnlyList<LabelConfig>? labels, string source) =>
        labels is null
            ? null
            : [.. labels.Select(label => new LabelSpec(
                label.Name ?? throw Bad(source, "a label has no name"),
                label.Color ?? throw Bad(source, "a label has no color"),
                label.Description ?? throw Bad(source, "a label has no description")))];

    // environments: name -> {wait_timer, reviewers}, in the file's order; a null wait_timer is 0.
    private static List<EnvironmentPolicy>? EnvironmentsOf(IReadOnlyDictionary<string, EnvironmentConfig>? environments, string source) =>
        environments is null
            ? null
            : [.. environments.Select(pair => new EnvironmentPolicy(pair.Key, pair.Value.WaitTimer ?? 0, Reviewers(pair.Key, pair.Value.Reviewers, source)))];

    // An omitted reviewers list is none; an explicit null is a mistake, not a way of saying none.
    private static List<string> Reviewers(string environment, JsonElement reviewers, string source) =>
        reviewers.ValueKind switch
        {
            JsonValueKind.Undefined => [],
            JsonValueKind.Null => throw Bad(source, $"environments.{environment}.reviewers is null"),
            JsonValueKind.Array => [.. reviewers.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw Bad(source, $"environments.{environment}.reviewers is not a list of strings"))],
            _ => throw Bad(source, $"environments.{environment}.reviewers is not a list of strings"),
        };

    private static PagesPolicy? PagesOf(PagesConfig? block, string source)
    {
        if (block is null)
        {
            return null;
        }

        var buildType = block.BuildType ?? "workflow";
        if (buildType is not ("workflow" or "legacy"))
        {
            throw Bad(source, $"pages.build_type '{buildType}' is neither 'workflow' nor 'legacy'");
        }

        return new(block.Enabled ?? false, buildType, block.Cname, block.HttpsEnforced ?? true);
    }

    private static ForkSpec? ForkOf(ForkConfig? block, string source) =>
        block is { } fork
            ? new(fork.Upstream ?? throw Bad(source, "fork has no upstream"), fork.UpstreamBranch ?? "main")
            : null;

    private static BranchCleanupRules? CleanupOf(BranchCleanupConfig? block)
    {
        if (block is null)
        {
            return null;
        }

        var defaults = BranchCleanupRules.Default;
        return new(
            block.Enabled ?? defaults.Enabled,
            block.Protected ?? defaults.Protected,
            block.ProtectOpenPrHeads ?? defaults.ProtectOpenPrHeads,
            block.ProtectPrefixes ?? defaults.ProtectPrefixes);
    }

    private static ScriptBanRules? ScriptBanOf(ScriptBanConfig? block)
    {
        if (block is null)
        {
            return null;
        }

        var defaults = ScriptBanRules.Default;
        return new(
            block.Enabled ?? defaults.Enabled,
            block.BannedExtensions ?? defaults.BannedExtensions,
            block.AllowedExceptions ?? defaults.AllowedExceptions,
            block.AllowedScriptExtensions ?? defaults.AllowedScriptExtensions,
            block.BannedScriptExtensions ?? defaults.BannedScriptExtensions);
    }

    // license: a bare type, or {"type": …}; an object without a type declares the default.
    private static string? LicenseOf(LicenseConfig? license) => license is null ? null : license.Type ?? string.Empty;

    // expected_owner: null declares "no expected owner": kept as "" so it still stops the lookup.
    private static string? ExpectedOwnerOf(JsonElement expectedOwner, string source) =>
        expectedOwner.ValueKind switch
        {
            JsonValueKind.Undefined => null,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.String => expectedOwner.GetString(),
            _ => throw Bad(source, "expected_owner is not a string"),
        };

    // The keys one of defaults, a profile or a repo override declares; null is "not declared here".
    private sealed record Layer(
        IReadOnlyList<string>? Languages,
        IReadOnlyList<GitignoreEntry>? GitignoreEntries,
        AgentsMdRules? AgentsMd,
        RepoSelectionRules? RepoSelection,
        IReadOnlyList<string>? AllowedOwners,
        string? ExpectedOwner)
    {
        public static Layer None { get; } = new(null, null, null, null, null, null);

        public string? ExpectedBaseBranch { get; init; }

        public MergeSettings? RepoSettings { get; init; }

        public IReadOnlyList<LabelSpec>? Labels { get; init; }

        public IReadOnlyList<EnvironmentPolicy>? Environments { get; init; }

        public IReadOnlyList<string>? RequiredChecks { get; init; }

        public PagesPolicy? Pages { get; init; }

        public ForkSpec? Fork { get; init; }

        public string? CopyrightHolder { get; init; }

        public string? ContactEmail { get; init; }

        public string? License { get; init; }

        public BranchCleanupRules? BranchCleanup { get; init; }

        public ScriptBanRules? ScriptBan { get; init; }

        public VercelGatingRules? Vercel { get; init; }

        public static Layer From(PolicyLayerConfig layer, string source)
        {
            ArgumentNullException.ThrowIfNull(layer);
            return new(
                layer.Languages,
                Entries(layer.GitignoreEntries, source),
                Hygiene(layer.AgentsMdHygiene, source),
                Selection(layer.RepoSelection),
                layer.AllowedOwners,
                ExpectedOwnerOf(layer.ExpectedOwner, source))
            {
                ExpectedBaseBranch = layer.ExpectedBaseBranch,
                RepoSettings = Merge(layer.RepoSettings),
                Labels = LabelsOf(layer.Labels, source),
                Environments = EnvironmentsOf(layer.Environments, source),
                RequiredChecks = layer.RequiredChecks,
                Pages = PagesOf(layer.Pages, source),
                Fork = ForkOf(layer.Fork, source),
                CopyrightHolder = layer.CopyrightHolder,
                ContactEmail = layer.ContactEmail,
                License = LicenseOf(layer.License),
                BranchCleanup = CleanupOf(layer.BranchCleanup),
                ScriptBan = ScriptBanOf(layer.ScriptBan),
                Vercel = layer.Vercel is { } vercel
                    ? new(vercel.TeamId ?? string.Empty, vercel.WindowDays ?? VercelGatingRules.Default.WindowDays)
                    : null,
            };
        }
    }
}

/// <summary>A declared GitHub App: <c>github_apps.{slug}</c>.</summary>
/// <param name="Slug">The app's slug, the key it is declared under.</param>
/// <param name="Name">Its display name; the slug when none is given.</param>
/// <param name="InstallPage">Where a person installs it.</param>
/// <param name="ConfigTemplate">The in-repo config file it reads, if any.</param>
/// <param name="Accounts">Its enrolment on each account, by account name.</param>
public sealed record AppPolicy(string Slug, string Name, string InstallPage, string? ConfigTemplate, IReadOnlyDictionary<string, AppAccountPolicy> Accounts);

/// <summary>Which repos an app should reach on one account.</summary>
/// <param name="Required">Whether its absence fails the audit.</param>
/// <param name="Repos">The repos it should reach, when not <paramref name="AllRepos"/>.</param>
/// <param name="AllRepos">Whether it should reach every repo of the account.</param>
/// <param name="DisallowRepos">Repos it must not reach; <c>exclude_repos</c> is the older name.</param>
public sealed record AppAccountPolicy(bool Required, IReadOnlyList<string> Repos, bool AllRepos, IReadOnlyList<string> DisallowRepos)
{
    /// <summary>The repos it should reach: <paramref name="available"/> (when <see cref="AllRepos"/>) or <see cref="Repos"/>, less <see cref="DisallowRepos"/>.</summary>
    public IReadOnlyList<string> EffectiveRepos(IReadOnlyList<string> available) =>
        [.. (AllRepos ? available : Repos).Where(repo => !DisallowRepos.Contains(repo))];
}

/// <summary>Which remote branches cleanup may delete once their work has landed (<c>branch_cleanup</c>).</summary>
/// <param name="Enabled">Whether cleanup runs for the repo at all.</param>
/// <param name="Protected">Branches never deleted, whatever their merge state.</param>
/// <param name="ProtectOpenPrHeads">Whether a branch with an open PR is kept.</param>
/// <param name="ProtectPrefixes">Prefixes bots recreate and manage their own branches under.</param>
public sealed record BranchCleanupRules(bool Enabled, IReadOnlyList<string> Protected, bool ProtectOpenPrHeads, IReadOnlyList<string> ProtectPrefixes)
{
    /// <summary>Cleanup on, <c>main</c> and <c>master</c> protected, open-PR heads kept.</summary>
    public static BranchCleanupRules Default { get; } = new(true, ["main", "master"], true, []);

    /// <summary>Whether <paramref name="branch"/> must never be deleted by cleanup.</summary>
    public bool IsProtected(string branch) =>
        Protected.Contains(branch) || ProtectPrefixes.Any(prefix => branch.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>The estate-wide ban on shell and PowerShell scripts, and its exceptions (<c>script_ban</c>).</summary>
/// <param name="Enabled">Whether the ban applies.</param>
/// <param name="BannedExtensions">The extensions banned anywhere in the repo.</param>
/// <param name="AllowedExceptions">Paths exempt from the ban.</param>
/// <param name="AllowedScriptExtensions">The script extensions allowed in <c>scripts/</c>, overriding the language presets.</param>
/// <param name="BannedScriptExtensions">The script extensions banned in <c>scripts/</c>, overriding the language presets.</param>
public sealed record ScriptBanRules(
    bool Enabled,
    IReadOnlyList<string> BannedExtensions,
    IReadOnlyList<string> AllowedExceptions,
    IReadOnlyList<string> AllowedScriptExtensions,
    IReadOnlyList<string> BannedScriptExtensions)
{
    /// <summary>The ban on, over <c>.sh .bash .zsh .ps1 .psm1</c>, with no exceptions.</summary>
    public static ScriptBanRules Default { get; } = new(true, [".sh", ".bash", ".zsh", ".ps1", ".psm1"], [], [], []);
}

/// <summary>The script extensions a repo allows and bans in <c>scripts/</c>.</summary>
/// <param name="AllowedExtensions">Extensions allowed.</param>
/// <param name="BannedExtensions">Extensions banned.</param>
public sealed record RepoScriptRules(IReadOnlyList<string> AllowedExtensions, IReadOnlyList<string> BannedExtensions);
