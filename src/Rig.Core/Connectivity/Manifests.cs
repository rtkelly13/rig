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
/// <c>pyproject.toml</c>, <c>*.csproj</c>, <c>Directory.Packages.props</c>, <c>Directory.Build.props</c> and, for projects
/// managed by Paket, <c>paket.dependencies</c> with each project's <c>paket.references</c>, which an
/// <see cref="IPaketManifests"/> reads with Paket's own parsers.
/// </summary>
public static partial class Manifests
{
    /// <summary>How a repo whose manifests could not be listed is warned about.</summary>
    public const string Unavailable = "Manifest inventory unavailable";

    /// <summary>The most manifests one repo may have before the scan is refused.</summary>
    public const int MaxManifests = 500;

    /// <summary>The largest manifest read, in bytes.</summary>
    public const long MaxBytes = 2_000_000;

    private static readonly (string Scope, string Key)[] NpmScopes =
        [("runtime", "dependencies"), ("development", "devDependencies"), ("peer", "peerDependencies"), ("optional", "optionalDependencies")];

    /// <summary>How long <c>git ls-files</c> may take before a checkout is reported unavailable.</summary>
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        ".git", "node_modules", ".venv", "venv", "temp", ".worktrees", "bin", "obj", "dist", "build", ".next",
        "fixtures", "__fixtures__", "testdata",
    };

    private static readonly HashSet<string> ManifestNames = new(StringComparer.Ordinal)
    {
        "package.json", "pyproject.toml", "Directory.Packages.props", "Directory.Build.props", "paket.dependencies", "paket.references",
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
    public static async Task<IReadOnlyList<string>> ListAsync(Git git, string root, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        var listed = await git.RunAsync(root, ["ls-files", "-z"], timeout ?? ListTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!listed.Ok)
        {
            var why = (listed.Err.Length > 0 ? listed.Err : listed.Out).Trim();
            throw new InvalidOperationException($"git ls-files exited with {listed.Code}: {why}");
        }

        var resolvedRoot = PathResolution.Resolve(root);
        var paths = listed.Out.Split('\0').Order(StringComparer.Ordinal)
            .Where(name => name.Length > 0 && !name.Split('/').Any(Skipped.Contains) && IsManifest(name.Split('/')[^1]))
            .Where(name => File.Exists(Path.Join(root, name)) && PathResolution.IsUnder(PathResolution.Resolve(Path.Join(root, name)), resolvedRoot))
            .ToList();

        return paths.Count > MaxManifests
            ? throw new InvalidDataException($"More than {MaxManifests} tracked package manifests; narrow the scan before indexing")
            : paths;
    }

    /// <summary>
    /// What <paramref name="repo"/>, checked out at <paramref name="root"/>, publishes and consumes, each relationship
    /// keeping the manifest it was read from. A manifest that cannot be read is a warning, not a failure.
    /// </summary>
    public static async Task<Inventory> ReadAsync(Git git, IPaketManifests paket, string root, string repo, TimeSpan? listTimeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paket);
        ArgumentNullException.ThrowIfNull(root);
        var inventory = new Inventory();
        try
        {
            inventory.PathsFound.AddRange(await ListAsync(git, root, listTimeout, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            inventory.WarningsFound.Add($"{Unavailable}: {e.Message}");
            return inventory;
        }

        var central = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var manifests = new List<(string Evidence, string Full)>();
        var paketFiles = new List<(string Evidence, string Full)>();
        foreach (var evidence in inventory.Paths)
        {
            var full = Path.GetFullPath(Path.Join(root, evidence));
            if (Path.GetFileName(full) is "paket.dependencies" or "paket.references")
            {
                paketFiles.Add((evidence, full));
            }
            else if (Path.GetFileName(full) == "Directory.Packages.props")
            {
                try
                {
                    var text = XmlComment().Replace(await ReadBoundedAsync(full, cancellationToken).ConfigureAwait(false), string.Empty);
                    var versions = Folder(central, Path.GetDirectoryName(full)!);
                    foreach (Match tag in PackageVersionTag().Matches(text))
                    {
                        versions[Canonical("nuget", Attribute(tag.Value, "Include"))] = Attribute(tag.Value, "Version");
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException)
                {
                    inventory.WarningsFound.Add($"{evidence}: {e.Message}");
                }
            }
            else
            {
                manifests.Add((evidence, full));
            }
        }

        await ReadPaketAsync(inventory, paket, paketFiles, repo, cancellationToken).ConfigureAwait(false);
        foreach (var (evidence, full) in manifests)
        {
            try
            {
                var text = await ReadBoundedAsync(full, cancellationToken).ConfigureAwait(false);
                if (full.EndsWith(".csproj", StringComparison.Ordinal) || full.EndsWith(".props", StringComparison.Ordinal))
                {
                    text = XmlComment().Replace(text, string.Empty);
                }

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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or DecoderFallbackException or JsonException or TomlException)
            {
                inventory.WarningsFound.Add($"{evidence}: {e.Message}");
            }
        }

        return inventory;
    }

    private static bool IsManifest(string file) => ManifestNames.Contains(file) || file.EndsWith(".csproj", StringComparison.Ordinal);

    private static async Task<string> ReadBoundedAsync(string full, CancellationToken cancellationToken) =>
        new FileInfo(full).Length > MaxBytes
            ? throw new InvalidDataException("Manifest exceeds 2 MB")
            : StrictUtf8.GetString(await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false));

    private static Dictionary<string, string> Folder(Dictionary<string, Dictionary<string, string>> central, string folder)
    {
        if (!central.TryGetValue(folder, out var versions))
        {
            central[folder] = versions = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return versions;
    }

    private static void ReadNpm(Inventory inventory, string text, string repo, string evidence)
    {
        // The whole manifest is validated before any of it counts: a malformed one adds nothing.
        var package = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
            ?? throw new InvalidDataException("expected a JSON object");
        var name = OptionalString(package, "name");
        var consumptions = NpmScopes
            .SelectMany(scope => StringMap(package, scope.Key).Select(dependency => (scope.Scope, dependency.Name, dependency.Version)))
            .Select(d => (d, alias: NpmAlias().Match(d.Version)))
            .Select(x => new Consumption("npm", Canonical("npm", x.alias.Success ? x.alias.Groups[1].Value : x.d.Name), repo, x.d.Version, x.d.Scope, evidence))
            .ToList();

        if (name.Length > 0)
        {
            inventory.PublicationsFound.Add(new Publication("npm", Canonical("npm", name), repo, evidence));
        }

        inventory.ConsumptionsFound.AddRange(consumptions);
    }

    private static void ReadPython(Inventory inventory, string text, string repo, string evidence)
    {
        var document = TomlSerializer.Deserialize<TomlTable>(text) ?? throw new InvalidDataException("expected a TOML document");
        if (!document.TryGetValue("project", out var value))
        {
            return;
        }

        var project = value as TomlTable ?? throw new InvalidDataException("project: expected a table");
        // As for npm, nothing counts until the whole project table has validated.
        var name = project.TryGetValue("name", out var named) ? named as string ?? throw new InvalidDataException("project.name: expected a string") : string.Empty;
        var requirements = Strings(project, "dependencies", "project.dependencies").Select(requirement => (scope: "runtime", requirement)).ToList();
        if (project.TryGetValue("optional-dependencies", out var optional))
        {
            var extras = optional as TomlTable ?? throw new InvalidDataException("project.optional-dependencies: expected a table");
            foreach (var (extra, _) in extras)
            {
                requirements.AddRange(Strings(extras, extra, $"project.optional-dependencies.{extra}").Select(requirement => (scope: $"optional:{extra}", requirement)));
            }
        }

        if (name.Length > 0)
        {
            inventory.PublicationsFound.Add(new Publication("pypi", Canonical("pypi", name), repo, evidence));
        }

        inventory.ConsumptionsFound.AddRange(requirements
            .Select(r => (r.scope, r.requirement, match: PythonName().Match(r.requirement)))
            .Where(r => r.match.Success)
            .Select(r => new Consumption("pypi", Canonical("pypi", r.match.Value), repo, r.requirement, r.scope, evidence)));
    }

    private static void ReadMsBuild(Inventory inventory, string text, string repo, string evidence, string full, Dictionary<string, Dictionary<string, string>> central)
    {
        if (PackageId().Match(text) is { Success: true } id && !id.Groups[1].Value.Contains("$(", StringComparison.Ordinal))
        {
            inventory.PublicationsFound.Add(new Publication("nuget", Canonical("nuget", id.Groups[1].Value), repo, evidence));
        }

        inventory.ConsumptionsFound.AddRange(NuGetReferences(text, repo, evidence, NearestVersions(full, central)));
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

    /// <summary>The versions set by the nearest <c>Directory.Packages.props</c> or <c>paket.dependencies</c> above <paramref name="full"/>.</summary>
    private static Dictionary<string, string> NearestVersions(string full, Dictionary<string, Dictionary<string, string>> central)
    {
        for (var folder = Path.GetDirectoryName(full); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (central.TryGetValue(folder, out var found))
            {
                return found;
            }
        }

        return new Dictionary<string, string>();
    }

    /// <summary>
    /// Reads every Paket file of a repo in one call to <paramref name="paket"/>. Each <c>paket.references</c> package takes
    /// its version from the nearest <c>paket.dependencies</c> above it: the locked version, else the constraint. A reader
    /// that cannot run skips the repo's Paket files with one warning.
    /// </summary>
    private static async Task ReadPaketAsync(Inventory inventory, IPaketManifests paket, List<(string Evidence, string Full)> files, string repo, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        var reading = await paket.ReadAsync([.. files.Select(file => file.Full)], cancellationToken).ConfigureAwait(false);
        if (reading.Unavailable is { } reason)
        {
            inventory.WarningsFound.Add($"Paket manifests skipped: {reason}");
            return;
        }

        var read = reading.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var readable = files
            .Select(file => (file.Evidence, file.Full, Reading: read.GetValueOrDefault(file.Full) ?? new PaketFile(file.Full, [], "the Paket sidecar did not answer for this file", null)))
            .ToList();
        inventory.WarningsFound.AddRange(readable
            .Where(file => file.Reading.Error is not null || file.Reading.Warning is not null)
            .Select(file => $"{file.Evidence}: {file.Reading.Error ?? file.Reading.Warning}"));

        var versions = readable
            .Where(file => Path.GetFileName(file.Full) == "paket.dependencies" && file.Reading.Error is null)
            .ToDictionary(
                file => Path.GetDirectoryName(file.Full)!,
                file => file.Reading.Groups
                    .SelectMany(group => group.Packages.Select(package => (Key: (group.Name, Canonical("nuget", package.Name)), Version: PaketRequirement(package.Version))))
                    .GroupBy(entry => entry.Key)
                    .ToDictionary(entries => entries.Key, entries => entries.Last().Version),
                StringComparer.Ordinal);
        inventory.ConsumptionsFound.AddRange(readable
            .Where(file => Path.GetFileName(file.Full) == "paket.references" && file.Reading.Error is null)
            .SelectMany(file => file.Reading.Groups.SelectMany(group => group.Packages.Select(package =>
            {
                var key = Canonical("nuget", package.Name);
                return new Consumption("nuget", key, repo, NearestPaketVersions(file.Full, versions).GetValueOrDefault((group.Name, key), "centrally resolved"), "runtime", file.Evidence);
            }))));
    }

    /// <summary>A Paket constraint as a requirement: an exact pin reads as its version, and no constraint as <c>unconstrained</c>.</summary>
    private static string PaketRequirement(string? version) =>
        version?.TrimStart('=', ' ') is { Length: > 0 } requirement ? requirement : "unconstrained";

    private static Dictionary<(string Group, string Package), string> NearestPaketVersions(string full, Dictionary<string, Dictionary<(string Group, string Package), string>> versions)
    {
        for (var folder = Path.GetDirectoryName(full); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (versions.TryGetValue(folder, out var found))
            {
                return found;
            }
        }

        return [];
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
