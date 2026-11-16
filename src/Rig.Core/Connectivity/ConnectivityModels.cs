namespace Rig.Core.Connectivity;

/// <summary>A package a repo publishes, and the manifest (or registry entry) that says so.</summary>
/// <param name="Registry"><c>npm</c>, <c>nuget</c> or <c>pypi</c>.</param>
/// <param name="Package">The package's canonical name (<see cref="Manifests.Canonical"/>).</param>
/// <param name="Repo">The publishing repo's identity (<see cref="EstateGraph.Slug"/>).</param>
/// <param name="Evidence">Where it was read.</param>
public sealed record Publication(string Registry, string Package, string Repo, string Evidence);

/// <summary>A package a repo consumes.</summary>
/// <param name="Registry"><c>npm</c>, <c>nuget</c> or <c>pypi</c>.</param>
/// <param name="Package">The package's canonical name.</param>
/// <param name="Repo">The consuming repo's identity.</param>
/// <param name="Requirement">The version range or requirement as written.</param>
/// <param name="Scope"><c>runtime</c>, <c>development</c>, <c>peer</c>, <c>optional</c> or <c>optional:&lt;extra&gt;</c>.</param>
/// <param name="Evidence">The manifest it was read from, relative to the repo.</param>
public sealed record Consumption(string Registry, string Package, string Repo, string Requirement, string Scope, string Evidence);

/// <summary>One explained connection between two repos: <paramref name="Source"/> provides what <paramref name="Target"/> uses.</summary>
/// <param name="Source">The providing repo.</param>
/// <param name="Target">The consuming repo.</param>
/// <param name="Registry">A package registry, or <c>project</c> for a project reference and <c>declared</c> for a <c>depends_on</c>.</param>
/// <param name="Package">The package, project file or declared name.</param>
/// <param name="Requirement">The requirement as written.</param>
/// <param name="Scope">The consumption scope.</param>
/// <param name="Evidence">Where the connection was read.</param>
public sealed record Dependency(string Source, string Target, string Registry, string Package, string Requirement, string Scope, string Evidence);

/// <summary>A managed repo as the graph shows it.</summary>
/// <param name="Id">Its identity, <c>owner/name</c> in lower case.</param>
/// <param name="Name">Its folder name.</param>
/// <param name="Description">What it is.</param>
/// <param name="Domain">Its domain.</param>
/// <param name="Stack">Its tech stack.</param>
/// <param name="Enabled">False for a retired project.</param>
/// <param name="Scanned">Whether its manifests were read.</param>
/// <param name="Manifests">How many manifests were read.</param>
/// <param name="Packages">The packages it publishes, sorted.</param>
/// <param name="Warnings">What could not be read.</param>
public sealed record GraphProject(
    string Id,
    string Name,
    string Description,
    string Domain,
    string Stack,
    bool Enabled,
    bool Scanned,
    int Manifests,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> Warnings);

/// <summary>A node of the condensed graph: one repo, or a cycle of repos that depend on each other.</summary>
/// <param name="Id">The repo's id, or <c>cycle:</c> and a hash of the members.</param>
/// <param name="Members">The repos it stands for, sorted.</param>
/// <param name="Cyclic">Whether it is a cycle.</param>
public sealed record Component(string Id, IReadOnlyList<string> Members, bool Cyclic);

/// <summary>An edge of the condensed graph.</summary>
/// <param name="Source">The providing component.</param>
/// <param name="Target">The consuming component.</param>
public sealed record ComponentEdge(string Source, string Target);

/// <summary>The condensed graph: a true DAG over components.</summary>
/// <param name="Components">Its nodes, sorted by id.</param>
/// <param name="Edges">Its edges, sorted.</param>
public sealed record Dag(IReadOnlyList<Component> Components, IReadOnlyList<ComponentEdge> Edges);

/// <summary>The answer to <c>GET /api/estate/graph</c>: every repo, every explained connection and their condensation.</summary>
/// <param name="Projects">The managed repos, in registry order.</param>
/// <param name="Dependencies">Every connection, with its evidence.</param>
/// <param name="Components">The condensed nodes.</param>
/// <param name="Edges">The condensed edges.</param>
/// <param name="ScannedAt">When the manifests were read, ISO 8601 in UTC.</param>
/// <param name="Warnings">Ambiguous publishers and unknown declared dependencies.</param>
public sealed record ConnectivityGraph(
    IReadOnlyList<GraphProject> Projects,
    IReadOnlyList<Dependency> Dependencies,
    IReadOnlyList<Component> Components,
    IReadOnlyList<ComponentEdge> Edges,
    string ScannedAt,
    IReadOnlyList<string> Warnings);
