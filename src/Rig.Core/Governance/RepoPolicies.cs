using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;

namespace Rig.Core.Governance;

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
    /// <summary>Whether <paramref name="fileName"/> is one of <see cref="DetectFiles"/>.</summary>
    public bool MatchesFile(string fileName) =>
        DetectFiles.Any(pattern => pattern == fileName || Glob.Matches(fileName, pattern, OperatingSystem.IsWindows()));
}

/// <summary>
/// <c>config/repo_policies.json</c>, as far as the <c>.gitignore</c> policy, repo selection and the AGENTS.md audit read it. A key resolves from the repo's
/// override, then the profile naming it, then <c>defaults</c>, taking the first that declares it (no deep merge).
/// Ported from <c>rig.repo_governance.policy_config</c>, whose other keys the repo-governance audits still read.
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
        List<(string, string)> ownerProfiles)
    {
        _defaults = defaults;
        _profiles = profiles;
        _overrides = overrides;
        _ownerProfiles = ownerProfiles;
        Presets = presets;
    }

    /// <summary>A policy that requires nothing.</summary>
    public static RepoPolicies Empty { get; } = new(Layer.None, [], [], [], []);

    /// <summary>The language presets, in the file's order.</summary>
    public IReadOnlyList<LanguagePreset> Presets { get; }

    /// <summary>
    /// The policy in <paramref name="path"/>. A missing or unparseable file requires nothing; a file of the wrong
    /// shape throws <see cref="InvalidDataException"/>, since falling back would silently drop protections.
    /// </summary>
    public static RepoPolicies Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty;
        }

        return Parse(root, path);
    }

    /// <summary>The policy in a parsed document; <paramref name="source"/> names it in errors.</summary>
    public static RepoPolicies Parse(JsonNode? root, string source)
    {
        var file = root as JsonObject ?? throw Bad(source, "the document is not an object");
        var profiles = Map(file, "profiles", source)
            .Select(pair => (pair.Key, Layer.From(pair.Value, source), (IReadOnlyList<string>)(Strings(pair.Value, "repos", source) ?? [])))
            .ToList();
        var declared = profiles.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var owners = Map(file, "owner_profiles", source)
            .Select(pair => (Owner: pair.Key, Profile: pair.Value?.GetValueKind() == JsonValueKind.String ? pair.Value.GetValue<string>() : throw Bad(source, $"owner_profiles.{pair.Key} is not a string")))
            .ToList();
        var unknown = owners
            .Where(pair => !declared.Contains(pair.Profile))
            .OrderBy(pair => pair.Owner, StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            throw Bad(source, "owner_profiles names undeclared profile(s): " + string.Join(", ", unknown.Select(pair => $"{pair.Owner} -> {pair.Profile}")));
        }

        return new(
            file["defaults"] is { } defaults ? Layer.From(defaults, source) : Layer.None,
            profiles,
            [.. Map(file, "repo_overrides", source).Select(pair => (pair.Key, Layer.From(pair.Value, source)))],
            [.. Map(file, "language_presets", source).Select(pair => Preset(pair.Key, pair.Value, source))],
            owners);
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

    private static LanguagePreset Preset(string id, JsonNode? node, string source)
    {
        var spec = node as JsonObject ?? throw Bad(source, $"language_presets.{id} is not an object");
        var name = Text(spec, "name", source) ?? string.Empty;
        return new(
            id,
            name.Length > 0 ? name : id,
            [.. (Strings(spec, "aliases", source) ?? []).Select(alias => alias.ToLowerInvariant())],
            Strings(spec, "detect_files", source) ?? [],
            Entries(spec, "gitignore_entries", source) ?? []);
    }

    private static List<GitignoreEntry>? Entries(JsonNode? owner, string key, string source)
    {
        if (owner?[key] is not { } node)
        {
            return null;
        }

        return node is JsonArray items
            ? [.. items.Select(item => Entry(item, source))]
            : throw Bad(source, $"{key} is not a list");
    }

    private static GitignoreEntry Entry(JsonNode? node, string source)
    {
        var spec = node as JsonObject ?? throw Bad(source, "a gitignore entry is not an object");
        var write = Strings(spec, "write", source) ?? [];
        var name = Text(spec, "name", source) ?? string.Empty;
        var mode = Text(spec, "match_mode", source) ?? "any";
        if (mode is not ("any" or "all"))
        {
            throw Bad(source, $"match_mode '{mode}' is neither 'any' nor 'all'");
        }

        return new(
            name.Length > 0 ? name : write.Count > 0 ? write[0] : "entry",
            Text(spec, "comment", source) ?? string.Empty,
            write,
            Strings(spec, "satisfied_by", source) ?? write,
            mode == "all");
    }

    private static JsonObject Map(JsonObject file, string key, string source) =>
        file[key] switch
        {
            null => [],
            JsonObject map => map,
            _ => throw Bad(source, $"{key} is not an object"),
        };

    private static string? Text(JsonNode? owner, string key, string source) =>
        owner?[key] switch
        {
            null => null,
            var node when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
            _ => throw Bad(source, $"{key} is not a string"),
        };

    private static List<string>? Strings(JsonNode? owner, string key, string source) =>
        owner?[key] switch
        {
            null => null,
            JsonArray items when items.All(item => item?.GetValueKind() == JsonValueKind.String) => [.. items.Select(item => item!.GetValue<string>())],
            _ => throw Bad(source, $"{key} is not a list of strings"),
        };

    private static InvalidDataException Bad(string source, string why) => new($"{source}: {why}");

    private static int? Int(JsonNode? owner, string key, string source) =>
        owner?[key] switch
        {
            null => null,
            var node when node.GetValueKind() == JsonValueKind.Number && node.AsValue().TryGetValue(out int value) => value,
            _ => throw Bad(source, $"{key} is not an integer"),
        };

    private static bool? Bool(JsonNode? owner, string key, string source) =>
        owner?[key] switch
        {
            null => null,
            var node when node.GetValueKind() is JsonValueKind.True or JsonValueKind.False => node.GetValue<bool>(),
            _ => throw Bad(source, $"{key} is not a boolean"),
        };

    private static JsonObject? Block(JsonObject layer, string key, string source) =>
        layer[key] switch
        {
            null => null,
            JsonObject block => block,
            _ => throw Bad(source, $"{key} is not an object"),
        };

    // agents_md_hygiene, whole: a field it leaves out takes the built-in default, as the Python's dataclass did.
    private static AgentsMdRules? Hygiene(JsonObject layer, string source)
    {
        if (Block(layer, "agents_md_hygiene", source) is not { } hygiene)
        {
            return null;
        }

        var defaults = AgentsMdRules.Default;
        var claudeMd = Text(hygiene, "claude_md", source) ?? defaults.ClaudeMd;
        if (claudeMd is not ("symlink" or "absent" or "any"))
        {
            throw Bad(source, $"claude_md '{claudeMd}' is not one of symlink, absent, any");
        }

        return new()
        {
            MaxLines = Int(hygiene, "max_lines", source) ?? defaults.MaxLines,
            ClaudeMd = claudeMd,
            BespokeAgentFiles = Strings(hygiene, "bespoke_agent_files", source) ?? defaults.BespokeAgentFiles,
            Require = Strings(hygiene, "require", source) ?? defaults.Require,
            CheckPaths = Bool(hygiene, "check_paths", source) ?? defaults.CheckPaths,
            ReportOnly = Strings(hygiene, "report_only", source) ?? defaults.ReportOnly,
            DocsIndexMinFiles = Int(hygiene, "docs_index_min_files", source) ?? defaults.DocsIndexMinFiles,
            MaxCodeBlockLines = Int(hygiene, "max_code_block_lines", source) ?? defaults.MaxCodeBlockLines,
        };
    }

    private static RepoSelectionRules? Selection(JsonObject layer, string source)
    {
        if (Block(layer, "repo_selection", source) is not { } selection)
        {
            return null;
        }

        var defaults = RepoSelectionRules.Default;
        return new(
            Bool(selection, "manage_forks", source) ?? defaults.ManageForks,
            Bool(selection, "require_push_access", source) ?? defaults.RequirePushAccess,
            Bool(selection, "exempt", source) ?? defaults.Exempt);
    }

    private static MergeSettings? Merge(JsonObject layer, string source)
    {
        if (Block(layer, "repo_settings", source) is not { } block)
        {
            return null;
        }

        var defaults = MergeSettings.Default;
        return new(
            Bool(block, "delete_branch_on_merge", source) ?? defaults.DeleteBranchOnMerge,
            Bool(block, "allow_squash_merge", source) ?? defaults.AllowSquashMerge,
            Bool(block, "allow_merge_commit", source) ?? defaults.AllowMergeCommit,
            Bool(block, "allow_rebase_merge", source) ?? defaults.AllowRebaseMerge,
            Bool(block, "allow_auto_merge", source) ?? defaults.AllowAutoMerge);
    }

    private static List<LabelSpec>? LabelsOf(JsonObject layer, string source) =>
        layer["labels"] switch
        {
            null => null,
            JsonArray items => [.. items.Select(item => item is JsonObject label
                ? new LabelSpec(
                    Text(label, "name", source) ?? throw Bad(source, "a label has no name"),
                    Text(label, "color", source) ?? throw Bad(source, "a label has no color"),
                    Text(label, "description", source) ?? throw Bad(source, "a label has no description"))
                : throw Bad(source, "a label is not an object"))],
            _ => throw Bad(source, "labels is not a list"),
        };

    // environments: name -> {wait_timer, reviewers}, in the file's order; a null wait_timer is 0.
    private static List<EnvironmentPolicy>? EnvironmentsOf(JsonObject layer, string source)
    {
        if (Block(layer, "environments", source) is not { } block)
        {
            return null;
        }

        return [.. block.Select(pair => pair.Value is JsonObject spec
            ? new EnvironmentPolicy(pair.Key, Int(spec, "wait_timer", source) ?? 0, Strings(spec, "reviewers", source) ?? [])
            : throw Bad(source, $"environments.{pair.Key} is not an object"))];
    }

    private static PagesPolicy? PagesOf(JsonObject layer, string source)
    {
        if (Block(layer, "pages", source) is not { } block)
        {
            return null;
        }

        var buildType = Text(block, "build_type", source) ?? "workflow";
        if (buildType is not ("workflow" or "legacy"))
        {
            throw Bad(source, $"pages.build_type '{buildType}' is neither 'workflow' nor 'legacy'");
        }

        return new(Bool(block, "enabled", source) ?? false, buildType, Text(block, "cname", source), Bool(block, "https_enforced", source) ?? true);
    }

    private static ForkSpec? ForkOf(JsonObject layer, string source) =>
        Block(layer, "fork", source) is { } block
            ? new(Text(block, "upstream", source) ?? throw Bad(source, "fork has no upstream"), Text(block, "upstream_branch", source) ?? "main")
            : null;

    // expected_owner: null declares "no expected owner", which the Python keeps as "" so it still stops the lookup.
    private static string? ExpectedOwnerOf(JsonObject layer, string source) =>
        layer.TryGetPropertyValue("expected_owner", out var node) ? node is null ? string.Empty : Text(layer, "expected_owner", source) : null;

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

        public static Layer From(JsonNode? node, string source)
        {
            var layer = node as JsonObject ?? throw Bad(source, "a policy layer is not an object");
            return new(
                Strings(layer, "languages", source),
                Entries(layer, "gitignore_entries", source),
                Hygiene(layer, source),
                Selection(layer, source),
                Strings(layer, "allowed_owners", source),
                ExpectedOwnerOf(layer, source))
            {
                ExpectedBaseBranch = Text(layer, "expected_base_branch", source),
                RepoSettings = Merge(layer, source),
                Labels = LabelsOf(layer, source),
                Environments = EnvironmentsOf(layer, source),
                RequiredChecks = Strings(layer, "required_checks", source),
                Pages = PagesOf(layer, source),
                Fork = ForkOf(layer, source),
            };
        }
    }
}
