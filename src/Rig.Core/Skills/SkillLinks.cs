using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Rig.Core.Roots;
using Rig.Core.Vault;

namespace Rig.Core.Skills;

/// <summary>One system's skills folder against the catalog.</summary>
/// <param name="System">The system.</param>
/// <param name="Folder">Its skills folder.</param>
/// <param name="Exists">Whether that folder exists.</param>
public sealed record SystemSkillsAudit(string System, string Folder, bool Exists)
{
    /// <summary>Links and catalog-named collisions found.</summary>
    public int Total { get; set; }

    /// <summary>Links leading to the catalog's folder for their name.</summary>
    public Collection<string> Valid { get; } = [];

    /// <summary>Links leading nowhere.</summary>
    public Collection<string> Broken { get; } = [];

    /// <summary>Links for a catalog name leading somewhere else.</summary>
    public Collection<string> Drifted { get; } = [];

    /// <summary>Catalog names with nothing in the folder.</summary>
    public Collection<string> Missing { get; } = [];

    /// <summary>Working links for names outside the catalog.</summary>
    public Collection<string> Stale { get; } = [];

    /// <summary>Real files or folders (agent-owned) that shadow a catalog name.</summary>
    public Collection<string> Collisions { get; } = [];

    /// <summary>Whether the folder exists and nothing is wrong in it.</summary>
    public bool Healthy =>
        Exists && Missing.Count == 0 && Broken.Count == 0 && Drifted.Count == 0 && Stale.Count == 0 && Collisions.Count == 0;
}

/// <summary>What one system's operation did: links made, counted or removed.</summary>
/// <param name="System">The system.</param>
/// <param name="Count">How many.</param>
public sealed record SystemCount(string System, int Count);

/// <summary>
/// Keeps each system's skills folder linked to the catalog. Only links are ever removed: a real file or folder in a
/// skills folder belongs to that agent (Claude Code keeps its own <c>synced</c> bucket there), except where sync must
/// put a catalog skill in its place. Ported from <c>rig.skills.engine</c>.
/// </summary>
/// <param name="places">Where the systems' folders and the activation ledger live.</param>
/// <param name="linker">Makes the links; when it cannot, the skill folder is copied instead.</param>
public sealed class SkillLinks(SkillPlaces places, IDirectoryLinker linker)
{
    private static readonly JsonSerializerOptions LedgerFormat = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private string Folder(string system) => SkillSystems.Directory(system, places.Home);

    /// <summary>Each of <paramref name="systems"/>' folders against <paramref name="catalog"/>.</summary>
    public IReadOnlyList<SystemSkillsAudit> Audit(IReadOnlyList<Skill> catalog, IReadOnlyList<string> systems)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(systems);
        var byName = catalog.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var audits = new List<SystemSkillsAudit>();
        foreach (var system in systems)
        {
            var folder = Folder(system);
            var audit = new SystemSkillsAudit(system, folder, Directory.Exists(folder));
            audits.Add(audit);
            if (!audit.Exists)
            {
                foreach (var skill in catalog)
                {
                    audit.Missing.Add(skill.Name);
                }

                continue;
            }

            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Entries(folder))
            {
                var name = Path.GetFileName(entry);
                if (!DirectoryLinks.IsLink(entry))
                {
                    if (byName.ContainsKey(name))
                    {
                        _ = present.Add(name);
                        audit.Total++;
                        audit.Collisions.Add(name);
                    }

                    continue;
                }

                _ = present.Add(name);
                audit.Total++;
                if (DirectoryLinks.Resolve(entry) is not { } resolved)
                {
                    audit.Broken.Add(name);
                }
                else if (!byName.TryGetValue(name, out var skill))
                {
                    audit.Stale.Add(name);
                }
                else if (DirectoryLinks.Same(resolved, skill.Folder, places.Windows))
                {
                    audit.Valid.Add(name);
                }
                else
                {
                    audit.Drifted.Add(name);
                }
            }

            foreach (var name in byName.Keys.Where(n => !present.Contains(n)).Order(PyText.CodePointOrder))
            {
                audit.Missing.Add(name);
            }
        }

        return audits;
    }

    /// <summary>
    /// Links every skill in <paramref name="catalog"/> into each system's folder, replacing whatever holds its name
    /// unless it already leads there, then with <paramref name="prune"/> removes the links for other names. A dry
    /// run changes nothing and counts every skill.
    /// </summary>
    public IReadOnlyList<SystemCount> Sync(IReadOnlyList<Skill> catalog, IReadOnlyList<string> systems, bool prune, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(systems);
        var active = catalog.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var counts = new List<SystemCount>();
        foreach (var system in systems)
        {
            var folder = Folder(system);
            if (dryRun)
            {
                counts.Add(new(system, catalog.Count));
                continue;
            }

            _ = Directory.CreateDirectory(folder);
            foreach (var skill in catalog)
            {
                Install(skill, Path.Join(folder, skill.Name));
            }

            if (prune)
            {
                foreach (var entry in Entries(folder).Where(e => !active.Contains(Path.GetFileName(e)) && DirectoryLinks.IsLink(e)))
                {
                    DirectoryLinks.RemoveLink(entry);
                }
            }

            counts.Add(new(system, catalog.Count));
        }

        return counts;
    }

    /// <summary>Removes each system's links that lead nowhere; a dry run only counts them.</summary>
    public IReadOnlyList<SystemCount> PruneBroken(IReadOnlyList<string> systems, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(systems);
        var counts = new List<SystemCount>();
        foreach (var system in systems)
        {
            var folder = Folder(system);
            var broken = Directory.Exists(folder)
                ? Directory.EnumerateFileSystemEntries(folder).Where(e => DirectoryLinks.IsLink(e) && DirectoryLinks.Resolve(e) is null).ToList()
                : [];
            if (!dryRun)
            {
                broken.ForEach(DirectoryLinks.RemoveLink);
            }

            counts.Add(new(system, broken.Count));
        }

        return counts;
    }

    /// <summary>The skills activated beyond the allowlist and not yet deactivated, sorted.</summary>
    public IReadOnlyList<string> Activated() =>
        [.. (SkillCatalog.LoadNames(places.Ledger) ?? []).Distinct(StringComparer.Ordinal).Order(PyText.CodePointOrder)];

    /// <summary>
    /// Links the named skills (or with <paramref name="all"/> every skill) into each system without pruning, and
    /// records those outside the allowlist so <see cref="Deactivate"/> can take them away again.
    /// </summary>
    /// <exception cref="ArgumentException">A name is not a skill in <paramref name="catalog"/>.</exception>
    public IReadOnlyList<SystemCount> Activate(SkillCatalog catalog, IReadOnlyList<string> names, bool all, IReadOnlyList<string> systems, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var known = catalog.Everything().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var selected = all ? [.. known.Order(PyText.CodePointOrder)] : Selected(names);
        var unknown = selected.Where(n => !known.Contains(n)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown skill(s): {string.Join(", ", unknown)}");
        }

        var counts = Sync(catalog.Discover(selected), systems, prune: false, dryRun);
        if (!dryRun)
        {
            SaveLedger([.. Activated(), .. selected.Where(n => !catalog.IsCurated(n))]);
        }

        return counts;
    }

    /// <summary>
    /// Removes the named activated skills' links (or with <paramref name="all"/> every activated one's) from each
    /// system, only where a link still leads to the skill, and forgets each one no system links any more.
    /// </summary>
    public IReadOnlyList<SystemCount> Deactivate(SkillCatalog catalog, IReadOnlyList<string> names, bool all, IReadOnlyList<string> systems, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(systems);
        var activated = Activated();
        var selected = (all ? activated : Selected(names)).Where(activated.Contains).ToList();
        var folders = catalog.Everything().ToDictionary(s => s.Name, s => s.Folder, StringComparer.Ordinal);
        var counts = new List<SystemCount>();
        foreach (var system in systems)
        {
            var folder = Folder(system);
            var removed = 0;
            if (Directory.Exists(folder))
            {
                foreach (var link in selected.Select(name => Path.Join(folder, name)).Where(l => Leads(l, folders)).ToList())
                {
                    removed++;
                    if (!dryRun)
                    {
                        DirectoryLinks.RemoveLink(link);
                    }
                }
            }

            counts.Add(new(system, removed));
        }

        if (!dryRun)
        {
            SaveLedger([.. activated.Where(name => !selected.Contains(name)
                || (folders.ContainsKey(name) && SkillSystems.All.Any(s => Leads(Path.Join(Folder(s), name), folders))))]);
        }

        return counts;
    }

    // A link at path whose name is a skill, leading to that skill's folder.
    private bool Leads(string path, Dictionary<string, string> folders) =>
        folders.TryGetValue(Path.GetFileName(path), out var expected)
        && DirectoryLinks.IsLink(path)
        && DirectoryLinks.Resolve(path) is { } resolved
        && DirectoryLinks.Same(resolved, expected, places.Windows);

    private static List<string> Selected(IReadOnlyList<string> names) =>
        [.. names.Select(PyText.Strip).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Order(PyText.CodePointOrder)];

    private void Install(Skill skill, string destination)
    {
        if (DirectoryLinks.Occupied(destination))
        {
            if (DirectoryLinks.IsLink(destination) && DirectoryLinks.Same(PathResolution.Resolve(destination), skill.Folder, places.Windows))
            {
                return;
            }

            DirectoryLinks.Remove(destination);
        }

        try
        {
            linker.Link(destination, skill.Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Neither a symlink nor a junction could be made: a copy still gives the agent the skill.
            DirectoryLinks.Copy(skill.Folder, destination);
        }
    }

    private void SaveLedger(IEnumerable<string> names)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(places.Ledger)!);
        var sorted = names.Distinct(StringComparer.Ordinal).Order(PyText.CodePointOrder).ToArray();
        File.WriteAllText(places.Ledger, JsonSerializer.Serialize(sorted, LedgerFormat) + "\n");
    }

    private static IEnumerable<string> Entries(string folder) =>
        Directory.EnumerateFileSystemEntries(folder)
            .Where(e => !Path.GetFileName(e).StartsWith('.'))
            .Order(PyText.CodePointOrder);
}
