using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Packages;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Core.Connectivity;

/// <summary>
/// The estate's connectivity graph: which managed repo provides what another consumes, each connection backed by the
/// manifest or registry entry it was read from. Built from local checkouts only, no network calls; publishers come from
/// the registry's <c>packages</c> and each repo's manifests, connections from a unique publisher to every consumer, plus
/// MSBuild project references and the registry's <c>depends_on</c>. Strongly connected repos are condensed into cycle nodes.
/// </summary>
/// <remarks>
/// A package two repos publish is not connected to anything; it is a warning instead, so no edge is guessed. The last
/// graph is kept as <c>temp/estate/connectivity.json</c> in the rig checkout and reused for <see cref="Reuse"/>.
/// </remarks>
/// <param name="git">Runs git: <c>ls-files</c> in each checkout, and the rig checkout's origin.</param>
/// <param name="time">The clock for <c>scanned_at</c> and the snapshot's age.</param>
public sealed class EstateGraph(Git git, TimeProvider time)
{
    /// <summary>How long a snapshot is reused before reads inventory the checkouts again.</summary>
    public static readonly TimeSpan Reuse = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The snapshot's shape: snake_case keys, as the React app reads the graph. A snapshot missing a field, or holding
    /// null where the graph has none, is refused and rebuilt.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private const string Unscanned = "Local checkout is unavailable; consumption was not scanned.";
    private const string DeclaredEvidence = "config/managed-projects.json · depends_on";

    // Rebuilds rewrite one snapshot; the web host serves requests concurrently, so one build runs at a time.
    private static readonly SemaphoreSlim Building = new(1, 1);

    /// <summary>The snapshot for <paramref name="registryFile"/>: <c>temp/estate/connectivity.json</c> in the checkout holding it.</summary>
    public static string SnapshotPath(string registryFile) =>
        Path.Join(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(registryFile))), "temp", "estate", "connectivity.json");

    /// <summary>A repo's identity: <c>owner/name</c> in lower case for a GitHub remote, else the value lower-cased without <c>.git</c>.</summary>
    public static string Slug(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (RepoSlug.FromRemote(value) is { } slug)
        {
            return slug.Key;
        }

        var lower = value.ToLowerInvariant();
        return lower.EndsWith(".git", StringComparison.Ordinal) ? lower[..^4] : lower;
    }

    /// <summary>
    /// The graph: the snapshot when it is younger than <see cref="Reuse"/> and <paramref name="refresh"/> is false, else
    /// a fresh <see cref="BuildAsync"/>.
    /// </summary>
    public async Task<ConnectivityGraph> LoadAsync(string registryFile, string estateDir, bool refresh, CancellationToken cancellationToken = default)
    {
        var snapshot = SnapshotPath(registryFile);
        if (!refresh && File.Exists(snapshot) && time.GetUtcNow() - File.GetLastWriteTimeUtc(snapshot) < Reuse)
        {
            try
            {
                if (JsonSerializer.Deserialize<ConnectivityGraph>(await File.ReadAllTextAsync(snapshot, cancellationToken).ConfigureAwait(false), JsonOptions) is { } kept)
                {
                    return kept;
                }
            }
            catch (JsonException)
            {
                // A torn or outdated snapshot is rebuilt.
            }
        }

        return await BuildAsync(registryFile, estateDir, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inventories every managed checkout under <paramref name="estateDir"/> and writes the snapshot.</summary>
    /// <exception cref="InvalidDataException">The registry is malformed.</exception>
    /// <exception cref="IOException">The registry is missing or the snapshot could not be written.</exception>
    public async Task<ConnectivityGraph> BuildAsync(string registryFile, string estateDir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registryFile);
        await Building.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var graph = await ScanAsync(registryFile, estateDir, cancellationToken).ConfigureAwait(false);
            var snapshot = SnapshotPath(registryFile);
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            var temporary = snapshot + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(graph, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, snapshot, overwrite: true);
            return graph;
        }
        finally
        {
            Building.Release();
        }
    }

    private async Task<ConnectivityGraph> ScanAsync(string registryFile, string estateDir, CancellationToken cancellationToken)
    {
        var config = Path.GetDirectoryName(Path.GetFullPath(registryFile))!;
        var checkout = Path.GetDirectoryName(config)!;
        var specs = ManagedProjects.Load(registryFile);
        var bindings = PackageBindings.Load(registryFile);
        var scopes = EstateScopes.Load(Path.Join(config, EstateScopes.FileName));
        var origin = await git.OutAsync(checkout, "config", "--get", "remote.origin.url").ConfigureAwait(false);
        var current = origin.Length > 0 ? Slug(origin) : string.Empty;

        // The rig checkout answers for itself; every other repo is where the estate layout puts it.
        var roots = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var identity = Slug(spec.Repo);
            roots[identity] = identity == current ? checkout : scopes.ProjectDir(estateDir, spec.Name, spec.Repo);
        }

        var publications = bindings
            .Select(binding => new Publication(
                binding.Spec.Registry,
                Manifests.Canonical(binding.Spec.Registry, binding.Spec.Name),
                Slug(binding.Repo),
                $"config/managed-projects.json · {binding.Spec.Manifest}"))
            .ToList();
        var consumptions = new List<Consumption>();
        var projects = new List<GraphProject>();
        var dependencies = new List<Dependency>();
        foreach (var spec in specs)
        {
            var identity = Slug(spec.Repo);
            var local = roots[identity];
            var inventory = Directory.Exists(local) ? await Manifests.ReadAsync(git, local, identity, cancellationToken).ConfigureAwait(false) : null;
            if (inventory is not null)
            {
                publications.AddRange(inventory.Publications);
                consumptions.AddRange(inventory.Consumptions);
                foreach (var reference in inventory.ProjectRefs)
                {
                    dependencies.AddRange(roots
                        .Where(upstream => upstream.Key != identity && PathResolution.IsUnder(reference.Target, PathResolution.Resolve(upstream.Value)))
                        .Select(upstream => new Dependency(upstream.Key, identity, "project", Path.GetFileName(reference.Target), "project reference", "runtime", reference.Evidence)));
                }
            }

            var provided = publications.Where(p => p.Repo == identity).Select(p => p.Package).Distinct().Order(StringComparer.Ordinal).ToList();
            projects.Add(new GraphProject(
                identity,
                spec.Name,
                spec.Description,
                spec.Domain,
                spec.Stack,
                spec.Enabled,
                inventory is { Scanned: true },
                inventory?.Paths.Count ?? 0,
                provided,
                inventory is null ? [Unscanned] : [.. inventory.Warnings]));
        }

        var publishers = publications
            .GroupBy(p => (p.Registry, p.Package))
            .Select(group => (group.Key, Repos: group.Select(p => p.Repo).Distinct().Order(StringComparer.Ordinal).ToList()))
            .ToList();
        var providers = publishers.Where(p => p.Repos.Count == 1).ToDictionary(p => p.Key, p => p.Repos[0]);
        dependencies.AddRange(consumptions
            .Where(c => providers.TryGetValue((c.Registry, c.Package), out var provider) && provider != c.Repo)
            .Select(c => new Dependency(providers[(c.Registry, c.Package)], c.Repo, c.Registry, c.Package, c.Requirement, c.Scope, c.Evidence))
            .Distinct()
            .OrderBy(d => d.Source, StringComparer.Ordinal)
            .ThenBy(d => d.Target, StringComparer.Ordinal)
            .ThenBy(d => d.Registry, StringComparer.Ordinal)
            .ThenBy(d => d.Package, StringComparer.Ordinal)
            .ThenBy(d => d.Scope, StringComparer.Ordinal)
            .ThenBy(d => d.Evidence, StringComparer.Ordinal)
            .ThenBy(d => d.Requirement, StringComparer.Ordinal));
        var warnings = publishers
            .Where(p => p.Repos.Count > 1)
            .OrderBy(p => p.Key.Registry, StringComparer.Ordinal)
            .ThenBy(p => p.Key.Package, StringComparer.Ordinal)
            .Select(p => $"Ambiguous publisher for {p.Key.Registry}:{p.Key.Package}; no connection was guessed.")
            .ToList();

        var known = projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            names[project.Name] = project.Id;
        }

        foreach (var (repo, dependsOn) in Declared(registryFile))
        {
            var target = Slug(repo);
            foreach (var upstream in dependsOn)
            {
                var source = names.TryGetValue(upstream, out var named) ? named : Slug(upstream);
                if (known.Contains(source) && source != target)
                {
                    dependencies.Add(new Dependency(source, target, "declared", upstream, "declared dependency", "runtime", DeclaredEvidence));
                }
                else
                {
                    warnings.Add($"Unknown or self dependency {upstream} for {target}");
                }
            }
        }

        var sorted = dependencies
            .OrderBy(d => d.Source, StringComparer.Ordinal)
            .ThenBy(d => d.Target, StringComparer.Ordinal)
            .ThenBy(d => d.Registry, StringComparer.Ordinal)
            .ThenBy(d => d.Package, StringComparer.Ordinal)
            .ThenBy(d => d.Evidence, StringComparer.Ordinal)
            .ToList();
        var dag = Condensation.Condense(projects, sorted);
        var scannedAt = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
        return new ConnectivityGraph(projects, sorted, dag.Components, dag.Edges, scannedAt, warnings);
    }

    // Each registry project's repo and depends_on; the registry is tracked, so a malformed one is an error to fix.
    private static List<(string Repo, IReadOnlyList<string> DependsOn)> Declared(string registryFile)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(registryFile));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{registryFile}: {e.Message}", e);
        }

        var projects = root?["projects"] as JsonArray ?? throw new InvalidDataException($"{registryFile}: projects: expected a list");
        var declared = new List<(string, IReadOnlyList<string>)>();
        foreach (var (node, index) in projects.Select((node, index) => (node, index)))
        {
            var where = $"{registryFile}: projects[{index}]";
            var project = node as JsonObject ?? throw new InvalidDataException($"{where}: expected an object");
            var repo = project["repo"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new InvalidDataException($"{where}.repo: expected a string");
            IReadOnlyList<string> dependsOn = project["depends_on"] switch
            {
                null => [],
                JsonArray list => [.. list.Select(item => item is JsonValue entry && entry.TryGetValue<string>(out var name) ? name : throw new InvalidDataException($"{where}.depends_on: expected strings"))],
                _ => throw new InvalidDataException($"{where}.depends_on: expected a list"),
            };
            declared.Add((repo, dependsOn));
        }

        return declared;
    }
}
