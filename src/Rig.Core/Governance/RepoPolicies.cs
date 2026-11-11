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

        public static Layer From(JsonNode? node, string source)
        {
            var layer = node as JsonObject ?? throw Bad(source, "a policy layer is not an object");
            return new(
                Strings(layer, "languages", source),
                Entries(layer, "gitignore_entries", source),
                Hygiene(layer, source),
                Selection(layer, source),
                Strings(layer, "allowed_owners", source),
                ExpectedOwnerOf(layer, source));
        }
    }
}
