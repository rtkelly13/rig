using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Rig.Core.Connectivity;

/// <summary>A project reference out of a manifest: the resolved project file and the manifest that names it.</summary>
/// <param name="Target">The referenced project file, resolved.</param>
/// <param name="Evidence">The manifest naming it, relative to the repo.</param>
public sealed record ProjectLink(string Target, string Evidence);

/// <summary>What one repo's tracked manifests publish and consume.</summary>
/// <param name="Publications">The packages it publishes.</param>
/// <param name="Consumptions">The packages it consumes.</param>
/// <param name="ProjectRefs">Its MSBuild project references.</param>
/// <param name="Paths">The manifests found, relative to the repo with forward slashes.</param>
/// <param name="Warnings">What could not be read.</param>
public sealed record Inventory(
    IReadOnlyList<Publication> Publications,
    IReadOnlyList<Consumption> Consumptions,
    IReadOnlyList<ProjectLink> ProjectRefs,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Warnings)
{
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
        IReadOnlyList<string> paths;
        try
        {
            paths = await ListAsync(git, root, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new Inventory([], [], [], [], [$"{Unavailable}: {e.Message}"]);
        }

        var files = paths.Select(evidence => (Evidence: evidence, Full: Path.GetFullPath(Path.Join(root, evidence)))).ToList();
        var props = await files
            .Where(file => Path.GetFileName(file.Full) == "Directory.Packages.props")
            .ToAsyncEnumerable()
            .Select(async ValueTask<PropsRead> (file, token) =>
            {
                try
                {
                    var text = XmlComment().Replace(await ReadTextAsync(file.Full, token).ConfigureAwait(false), string.Empty);
                    var versions = PackageVersionTag().Matches(text)
                        .GroupBy(tag => Canonical("nuget", Attribute(tag.Value, "Include")), StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => Attribute(group.Last().Value, "Version"), StringComparer.Ordinal);
                    return new PropsRead(Path.GetDirectoryName(file.Full)!, versions, null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException)
                {
                    return new PropsRead(null, [], $"{file.Evidence}: {e.Message}");
                }
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var central = props
            .Where(read => read.Folder is not null)
            .ToDictionary(read => read.Folder!, read => read.Versions, StringComparer.Ordinal);

        var reads = await files
            .Where(file => Path.GetFileName(file.Full) != "Directory.Packages.props")
            .ToAsyncEnumerable()
            .Select(async ValueTask<ManifestRead> (file, token) =>
            {
                try
                {
                    var text = await ReadTextAsync(file.Full, token).ConfigureAwait(false);
                    if (file.Full.EndsWith(".csproj", StringComparison.Ordinal) || file.Full.EndsWith(".props", StringComparison.Ordinal))
                    {
                        text = XmlComment().Replace(text, string.Empty);
                    }

                    return new ManifestRead(
                        Path.GetFileName(file.Full) switch
                        {
                            "package.json" => ReadNpm(text, repo, file.Evidence),
                            "pyproject.toml" => ReadPython(text, repo, file.Evidence),
                            _ => ReadMsBuild(text, repo, file.Evidence, file.Full, central),
                        },
                        null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException or JsonException or TomlException)
                {
                    return new ManifestRead(Parsed.None, $"{file.Evidence}: {e.Message}");
                }
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new Inventory(
            [.. reads.SelectMany(read => read.Found.Publications)],
            [.. reads.SelectMany(read => read.Found.Consumptions)],
            [.. reads.SelectMany(read => read.Found.ProjectRefs)],
            paths,
            [.. props.Select(read => read.Warning).Concat(reads.Select(read => read.Warning)).OfType<string>()]);
    }

    // A manifest's text, refused when over the size cap.
    private static async Task<string> ReadTextAsync(string full, CancellationToken cancellationToken)
    {
        if (new FileInfo(full).Length > MaxBytes)
        {
            throw new InvalidDataException("Manifest exceeds 2 MB");
        }

        var bytes = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        return StrictUtf8.GetString(bytes);
    }

    private sealed record PropsRead(string? Folder, Dictionary<string, string> Versions, string? Warning);

    private sealed record ManifestRead(Parsed Found, string? Warning);

    private sealed record Parsed(IReadOnlyList<Publication> Publications, IReadOnlyList<Consumption> Consumptions, IReadOnlyList<ProjectLink> ProjectRefs)
    {
        public static Parsed None { get; } = new([], [], []);
    }

    private static Parsed ReadNpm(string text, string repo, string evidence)
    {
        // The whole manifest is validated before any of it counts: a malformed one adds nothing.
        var package = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
            ?? throw new InvalidDataException("expected a JSON object");
        var name = OptionalString(package, "name");
        var consumptions = new[] { ("runtime", "dependencies"), ("development", "devDependencies"), ("peer", "peerDependencies"), ("optional", "optionalDependencies") }
            .SelectMany(kind => StringMap(package, kind.Item2).Select(entry => (Scope: kind.Item1, entry.Name, entry.Version)))
            .Select(item =>
            {
                var alias = NpmAlias().Match(item.Version);
                return new Consumption("npm", Canonical("npm", alias.Success ? alias.Groups[1].Value : item.Name), repo, item.Version, item.Scope, evidence);
            })
            .ToList();

        return new Parsed(
            [.. (name.Length > 0 ? new[] { new Publication("npm", Canonical("npm", name), repo, evidence) } : [])],
            consumptions,
            []);
    }

    private static Parsed ReadPython(string text, string repo, string evidence)
    {
        var document = TomlSerializer.Deserialize<TomlTable>(text) ?? throw new InvalidDataException("expected a TOML document");
        if (!document.TryGetValue("project", out var value))
        {
            return Parsed.None;
        }

        var project = value as TomlTable ?? throw new InvalidDataException("project: expected a table");
        // As for npm, nothing counts until the whole project table has validated.
        var name = project.TryGetValue("name", out var named) ? named as string ?? throw new InvalidDataException("project.name: expected a string") : string.Empty;
        var extras = project.TryGetValue("optional-dependencies", out var optional)
            ? optional as TomlTable ?? throw new InvalidDataException("project.optional-dependencies: expected a table")
            : [];
        (string Scope, string Requirement)[] requirements =
        [
            .. Strings(project, "dependencies", "project.dependencies").Select(requirement => ("runtime", requirement)),
            .. extras.SelectMany(extra => Strings(extras, extra.Key, $"project.optional-dependencies.{extra.Key}").Select(requirement => ($"optional:{extra.Key}", requirement))),
        ];

        return new Parsed(
            [.. (name.Length > 0 ? new[] { new Publication("pypi", Canonical("pypi", name), repo, evidence) } : [])],
            [.. requirements
                .Where(item => PythonName().IsMatch(item.Requirement))
                .Select(item => new Consumption("pypi", Canonical("pypi", PythonName().Match(item.Requirement).Value), repo, item.Requirement, item.Scope, evidence))],
            []);
    }

    private static Parsed ReadMsBuild(string text, string repo, string evidence, string full, Dictionary<string, Dictionary<string, string>> central)
    {
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

        return new Parsed(
            [.. (PackageId().Match(text) is { Success: true } id && !id.Groups[1].Value.Contains("$(", StringComparison.Ordinal)
                ? new[] { new Publication("nuget", Canonical("nuget", id.Groups[1].Value), repo, evidence) }
                : [])],
            NuGetReferences(text, repo, evidence, versions),
            [.. ProjectReferenceTag().Matches(text)
                .Select(reference => Attribute(reference.Value, "Include"))
                .Where(target => target.Length > 0 && !target.Contains("$(", StringComparison.Ordinal))
                .Select(target => new ProjectLink(PathResolution.Resolve(Path.Join(Path.GetDirectoryName(full), target.Replace('\\', '/'))), evidence))]);
    }

    private static List<Consumption> NuGetReferences(string text, string repo, string evidence, IReadOnlyDictionary<string, string> versions)
    {
        // A later reference to the same package replaces an earlier one in place.
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

    private static string OptionalString(JsonObject node, string key) =>
        !node.ContainsKey(key)
            ? string.Empty
            : JsonShape.FieldText(node, key) ?? throw new InvalidDataException($"{key}: expected a string");

    private static IEnumerable<(string Name, string Version)> StringMap(JsonObject node, string key)
    {
        if (!node.ContainsKey(key))
        {
            return [];
        }

        var map = JsonShape.FieldObject(node, key) ?? throw new InvalidDataException($"{key}: expected an object");
        return [.. map.Select(pair => (pair.Key, JsonShape.AsText(pair.Value) ?? throw new InvalidDataException($"{key}.{pair.Key}: expected a string")))];
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
