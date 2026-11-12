using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Tomlyn;
using Tomlyn.Model;

namespace Rig.Core.Connectivity;

/// <summary>A project reference out of a manifest: the resolved project file and the manifest that names it.</summary>
/// <param name="Target">The referenced project file, resolved.</param>
/// <param name="Evidence">The manifest naming it, relative to the repo.</param>
public sealed record ProjectLink(string Target, string Evidence);

/// <summary>What one repo's tracked manifests publish and consume.</summary>
public sealed class Inventory
{
    /// <summary>The packages it publishes.</summary>
    public IReadOnlyList<Publication> Publications => PublicationsFound;

    internal List<Publication> PublicationsFound { get; } = [];

    /// <summary>The packages it consumes.</summary>
    public IReadOnlyList<Consumption> Consumptions => ConsumptionsFound;

    internal List<Consumption> ConsumptionsFound { get; } = [];

    /// <summary>Its MSBuild project references.</summary>
    public IReadOnlyList<ProjectLink> ProjectRefs => ProjectRefsFound;

    internal List<ProjectLink> ProjectRefsFound { get; } = [];

    /// <summary>The manifests found, relative to the repo with forward slashes.</summary>
    public IReadOnlyList<string> Paths => PathsFound;

    internal List<string> PathsFound { get; } = [];

    /// <summary>What could not be read.</summary>
    public IReadOnlyList<string> Warnings => WarningsFound;

    internal List<string> WarningsFound { get; } = [];

    /// <summary>Whether the manifests were listed at all.</summary>
    public bool Scanned => !Warnings.Any(warning => warning.StartsWith(Manifests.Unavailable, StringComparison.Ordinal));
}

/// <summary>
/// Reads package declarations and consumption from a repo's tracked, bounded manifests: <c>package.json</c>,
/// <c>pyproject.toml</c>, <c>*.csproj</c>, <c>Directory.Packages.props</c> and <c>Directory.Build.props</c>.
/// </summary>
public static partial class Manifests
{
    /// <summary>How a repo whose manifests could not be listed is warned about.</summary>
    public const string Unavailable = "Manifest inventory unavailable";

    /// <summary>The most manifests one repo may have before the scan is refused.</summary>
    public const int MaxManifests = 500;

    /// <summary>The largest manifest read, in bytes.</summary>
    public const long MaxBytes = 2_000_000;

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        ".git", "node_modules", ".venv", "venv", "temp", ".worktrees", "bin", "obj", "dist", "build", ".next",
        "fixtures", "__fixtures__", "testdata",
    };

    private static readonly HashSet<string> ManifestNames = new(StringComparer.Ordinal)
    {
        "package.json", "pyproject.toml", "Directory.Packages.props", "Directory.Build.props",
    };

    /// <summary>A package's identity: Python's names normalise <c>-_.</c> runs to <c>-</c>; every registry ignores case.</summary>
    public static string Canonical(string registry, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return registry == "pypi" ? PythonSeparators().Replace(name, "-").ToLowerInvariant() : name.ToLowerInvariant();
    }

    /// <summary>
    /// The manifests git tracks in <paramref name="root"/>, sorted: git's inventory skips caches and worktrees and never
    /// follows a link out of the repo. Throws when git cannot list them or there are more than <see cref="MaxManifests"/>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListAsync(Git git, string root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        var listed = await git.RunAsync(root, ["ls-files", "-z"], ListTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!listed.Ok)
        {
            var why = (listed.Err.Length > 0 ? listed.Err : listed.Out).Trim();
            throw new InvalidOperationException($"git ls-files exited with {listed.Code}: {why}");
        }

        var resolvedRoot = PathResolution.Resolve(root);
        var paths = new List<string>();
        foreach (var name in listed.Out.Split('\0').Order(StringComparer.Ordinal))
        {
            var parts = name.Split('/');
            if (name.Length == 0 || parts.Any(Skipped.Contains))
            {
                continue;
            }

            var file = parts[^1];
            if (!ManifestNames.Contains(file) && !file.EndsWith(".csproj", StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = Path.Join(root, name);
            if (File.Exists(candidate) && PathResolution.IsUnder(PathResolution.Resolve(candidate), resolvedRoot))
            {
                paths.Add(name);
            }
        }

        return paths.Count > MaxManifests
            ? throw new InvalidDataException($"More than {MaxManifests} tracked package manifests; narrow the scan before indexing")
            : paths;
    }

    /// <summary>
    /// What <paramref name="repo"/>, checked out at <paramref name="root"/>, publishes and consumes, each relationship
    /// keeping the manifest it was read from. A manifest that cannot be read is a warning, not a failure.
    /// </summary>
    public static async Task<Inventory> ReadAsync(Git git, string root, string repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var inventory = new Inventory();
        try
        {
            inventory.PathsFound.AddRange(await ListAsync(git, root, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            inventory.WarningsFound.Add($"{Unavailable}: {e.Message}");
            return inventory;
        }

        var central = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var texts = new List<(string Evidence, string Full, string Text)>();
        foreach (var evidence in inventory.Paths)
        {
            var full = Path.GetFullPath(Path.Join(root, evidence));
            try
            {
                if (new FileInfo(full).Length > MaxBytes)
                {
                    throw new InvalidDataException("Manifest exceeds 2 MB");
                }

                var text = await File.ReadAllTextAsync(full, StrictUtf8, cancellationToken).ConfigureAwait(false);
                if (full.EndsWith(".csproj", StringComparison.Ordinal) || full.EndsWith(".props", StringComparison.Ordinal))
                {
                    text = XmlComment().Replace(text, string.Empty);
                }

                texts.Add((evidence, full, text));
                if (Path.GetFileName(full) == "Directory.Packages.props")
                {
                    var versions = Folder(central, Path.GetDirectoryName(full)!);
                    foreach (Match tag in PackageVersionTag().Matches(text))
                    {
                        versions[Canonical("nuget", Attribute(tag.Value, "Include"))] = Attribute(tag.Value, "Version");
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException)
            {
                inventory.WarningsFound.Add($"{evidence}: {e.Message}");
            }
        }

        foreach (var (evidence, full, text) in texts)
        {
            try
            {
                switch (Path.GetFileName(full))
                {
                    case "package.json":
                        ReadNpm(inventory, text, repo, evidence);
                        break;
                    case "pyproject.toml":
                        ReadPython(inventory, text, repo, evidence);
                        break;
                    default:
                        ReadMsBuild(inventory, text, repo, evidence, full, central);
                        break;
                }
            }
            catch (Exception e) when (e is InvalidDataException or JsonException or TomlException)
            {
                inventory.WarningsFound.Add($"{evidence}: {e.Message}");
            }
        }

        return inventory;
    }

    private static Dictionary<string, string> Folder(Dictionary<string, Dictionary<string, string>> central, string folder)
    {
        if (!central.TryGetValue(folder, out var versions))
        {
            versions = new Dictionary<string, string>(StringComparer.Ordinal);
            central[folder] = versions;
        }

        return versions;
    }

    private static void ReadNpm(Inventory inventory, string text, string repo, string evidence)
    {
        var package = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("expected a JSON object");
        var name = OptionalString(package, "name");
        if (name.Length > 0)
        {
            inventory.PublicationsFound.Add(new Publication("npm", Canonical("npm", name), repo, evidence));
        }

        foreach (var (scope, key) in new[] { ("runtime", "dependencies"), ("development", "devDependencies"), ("peer", "peerDependencies"), ("optional", "optionalDependencies") })
        {
            foreach (var (dependency, version) in StringMap(package, key))
            {
                var alias = NpmAlias().Match(version);
                inventory.ConsumptionsFound.Add(new Consumption("npm", Canonical("npm", alias.Success ? alias.Groups[1].Value : dependency), repo, version, scope, evidence));
            }
        }
    }

    private static void ReadPython(Inventory inventory, string text, string repo, string evidence)
    {
        var document = TomlSerializer.Deserialize<TomlTable>(text) ?? throw new InvalidDataException("expected a TOML document");
        if (!document.TryGetValue("project", out var value))
        {
            return;
        }

        var project = value as TomlTable ?? throw new InvalidDataException("project: expected a table");
        var name = project.TryGetValue("name", out var named) ? named as string ?? throw new InvalidDataException("project.name: expected a string") : string.Empty;
        if (name.Length > 0)
        {
            inventory.PublicationsFound.Add(new Publication("pypi", Canonical("pypi", name), repo, evidence));
        }

        var requirements = Strings(project, "dependencies", "project.dependencies").Select(requirement => ("runtime", requirement)).ToList();
        if (project.TryGetValue("optional-dependencies", out var optional))
        {
            var extras = optional as TomlTable ?? throw new InvalidDataException("project.optional-dependencies: expected a table");
            foreach (var (extra, _) in extras)
            {
                requirements.AddRange(Strings(extras, extra, $"project.optional-dependencies.{extra}").Select(requirement => ($"optional:{extra}", requirement)));
            }
        }

        foreach (var (scope, requirement) in requirements)
        {
            if (PythonName().Match(requirement) is { Success: true } match)
            {
                inventory.ConsumptionsFound.Add(new Consumption("pypi", Canonical("pypi", match.Value), repo, requirement, scope, evidence));
            }
        }
    }

    private static void ReadMsBuild(Inventory inventory, string text, string repo, string evidence, string full, Dictionary<string, Dictionary<string, string>> central)
    {
        if (PackageId().Match(text) is { Success: true } id && !id.Groups[1].Value.Contains("$(", StringComparison.Ordinal))
        {
            inventory.PublicationsFound.Add(new Publication("nuget", Canonical("nuget", id.Groups[1].Value), repo, evidence));
        }

        // The nearest Directory.Packages.props above the project sets its central versions.
        IReadOnlyDictionary<string, string> versions = new Dictionary<string, string>();
        for (var folder = Path.GetDirectoryName(full); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (central.TryGetValue(folder, out var found))
            {
                versions = found;
                break;
            }
        }

        inventory.ConsumptionsFound.AddRange(NuGetReferences(text, repo, evidence, versions));
        foreach (Match reference in ProjectReferenceTag().Matches(text))
        {
            var target = Attribute(reference.Value, "Include");
            if (target.Length > 0 && !target.Contains("$(", StringComparison.Ordinal))
            {
                var resolved = PathResolution.Resolve(Path.Join(Path.GetDirectoryName(full), target.Replace('\\', '/')));
                inventory.ProjectRefsFound.Add(new ProjectLink(resolved, evidence));
            }
        }
    }

    private static List<Consumption> NuGetReferences(string text, string repo, string evidence, IReadOnlyDictionary<string, string> versions)
    {
        // A later reference to the same package replaces an earlier one in place, as a Python dict does.
        var references = new OrderedDictionary<string, Consumption>(StringComparer.Ordinal);
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in PackageReference().Matches(text))
        {
            var (tag, body) = (match.Groups[1].Value, match.Groups[2].Value);
            var name = Attribute(tag, "Include");
            var update = Attribute(tag, "Update");
            var version = Metadata(tag, body, "VersionOverride") is { Length: > 0 } overridden ? overridden : Metadata(tag, body, "Version");
            if (name.Length > 0 && !name.Contains("$(", StringComparison.Ordinal))
            {
                var key = Canonical("nuget", name);
                var requirement = version.Length > 0 ? version : versions.GetValueOrDefault(key, "centrally resolved");
                references[key] = new Consumption("nuget", key, repo, requirement, "runtime", evidence);
            }
            else if (update.Length > 0 && !update.Contains("$(", StringComparison.Ordinal) && version.Length > 0)
            {
                updates[Canonical("nuget", update)] = version;
            }
        }

        return [.. references.Select(pair => updates.TryGetValue(pair.Key, out var updated) ? pair.Value with { Requirement = updated } : pair.Value)];
    }

    private static string Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"\b{Regex.Escape(name)}\s*=\s*['""]([^'""]+)['""]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string Metadata(string tag, string body, string name)
    {
        var attribute = Attribute(tag, name);
        if (attribute.Length > 0)
        {
            return attribute;
        }

        var child = Regex.Match(body, $@"<{name}>\s*([^<]+?)\s*</{name}>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return child.Success ? child.Groups[1].Value : string.Empty;
    }

    private static string OptionalString(JsonObject node, string key) => node[key] switch
    {
        null when !node.ContainsKey(key) => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new InvalidDataException($"{key}: expected a string"),
    };

    private static IEnumerable<(string Name, string Version)> StringMap(JsonObject node, string key)
    {
        if (!node.ContainsKey(key))
        {
            return [];
        }

        var map = node[key] as JsonObject ?? throw new InvalidDataException($"{key}: expected an object");
        return [.. map.Select(pair => (pair.Key, pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new InvalidDataException($"{key}.{pair.Key}: expected a string")))];
    }

    private static IEnumerable<string> Strings(TomlTable table, string key, string where)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return [];
        }

        var array = value as TomlArray ?? throw new InvalidDataException($"{where}: expected an array");
        return [.. array.Select(item => item as string ?? throw new InvalidDataException($"{where}: expected strings"))];
    }

    [GeneratedRegex(@"[-_.]+", RegexOptions.CultureInvariant)]
    private static partial Regex PythonSeparators();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*", RegexOptions.CultureInvariant)]
    private static partial Regex PythonName();

    [GeneratedRegex(@"\Anpm:((?:@[^/\s]+/)?[^@\s]+)(?:@|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NpmAlias();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex XmlComment();

    [GeneratedRegex(@"<PackageVersion\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersionTag();

    [GeneratedRegex(@"<ProjectReference\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectReferenceTag();

    [GeneratedRegex(@"<PackageId>\s*([^<]+?)\s*</PackageId>", RegexOptions.CultureInvariant)]
    private static partial Regex PackageId();

    [GeneratedRegex(@"<PackageReference\b([^>]*?)(?:/\s*>|>(.*?)</PackageReference\s*>)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PackageReference();
}
