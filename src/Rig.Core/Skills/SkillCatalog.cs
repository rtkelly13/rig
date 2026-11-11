using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;
using Rig.Core.Roots;
using Rig.Core.Vault;

namespace Rig.Core.Skills;

/// <summary>The agent CLIs whose skills folders rig keeps linked to the catalog.</summary>
public static class SkillSystems
{
    /// <summary>Every system, in the order reports list them; <c>all</c> selects these.</summary>
    public static IReadOnlyList<string> All { get; } = ["opencode", "claude", "codex", "antigravity", "agents"];

    /// <summary>The systems used when none is named: every one but the cross-agent <c>agents</c> folder.</summary>
    public static IReadOnlyList<string> Primary { get; } = ["opencode", "claude", "codex", "antigravity"];

    /// <summary>What reports call <paramref name="system"/>.</summary>
    public static string DisplayName(string system) => system switch
    {
        "opencode" => "OpenCode",
        "claude" => "Claude Code",
        "codex" => "OpenAI Codex",
        "antigravity" => "Google Antigravity",
        "agents" => "Cross-Agent Standard",
        _ => system,
    };

    /// <summary>The global skills folder <paramref name="system"/> reads under <paramref name="home"/>.</summary>
    public static string Directory(string system, string home) => system switch
    {
        "opencode" => Path.Join(home, ".config", "opencode", "skills"),
        "claude" => Path.Join(home, ".claude", "skills"),
        "codex" => Path.Join(home, ".codex", "skills"),
        "antigravity" => Path.Join(home, ".gemini", "config", "skills"),
        "agents" => Path.Join(home, ".agents", "skills"),
        _ => throw new ArgumentOutOfRangeException(nameof(system), system, "Not a skills system."),
    };

    /// <summary>
    /// The systems <paramref name="names"/> select, or null with the offending name in <paramref name="unknown"/>.
    /// None selects <see cref="Primary"/>; <c>all</c> anywhere selects <see cref="All"/>. Names are trimmed and lower-cased.
    /// </summary>
    public static IReadOnlyList<string>? Resolve(IReadOnlyList<string> names, out string? unknown)
    {
        ArgumentNullException.ThrowIfNull(names);
        unknown = null;
        if (names.Count == 0)
        {
            return Primary;
        }

        var resolved = new List<string>();
        foreach (var value in names)
        {
            var name = value.Trim().ToLowerInvariant();
            if (name == "all")
            {
                return All;
            }

            if (!All.Contains(name))
            {
                unknown = value;
                return null;
            }

            resolved.Add(name);
        }

        return resolved;
    }
}

/// <summary>A discovered skill.</summary>
/// <param name="Name">The name its <c>SKILL.md</c> declares, else its folder's name.</param>
/// <param name="Description">Its one-line description, whitespace collapsed.</param>
/// <param name="Folder">Its folder, every link followed: what a system's link must lead to.</param>
/// <param name="Source">Where it came from: a source's label, or the vendor of a vendored skill.</param>
public sealed record Skill(string Name, string Description, string Folder, string Source);

/// <summary>A folder searched for skills, and the label its skills are reported under.</summary>
/// <param name="Name">The label.</param>
/// <param name="Path">The folder.</param>
public sealed record SkillSource(string Name, string Path);

/// <summary>Where the skills tools read and write.</summary>
/// <param name="Checkout">The rig checkout: its <c>skills/</c> is the canonical catalog, its <c>config/</c> the allowlists.</param>
/// <param name="Home">The home folder holding each system's skills folder, the user's own skills and the activation ledger.</param>
/// <param name="WorkingDirectory">Where a repository's own <c>skills/</c> is looked for.</param>
/// <param name="Windows">Whether names match and paths compare ignoring case.</param>
public sealed record SkillPlaces(string Checkout, string Home, string WorkingDirectory, bool Windows)
{
    /// <summary>The canonical catalog.</summary>
    public string CanonicalSkills => Path.Join(Checkout, "skills");

    /// <summary>Where vendored skills are extracted to (gitignored).</summary>
    public string Vendored => Path.Join(CanonicalSkills, "vendored");

    /// <summary>The estate allowlist: which catalog skills are linked by default.</summary>
    public string Allowlist => Path.Join(Checkout, "config", "skills-allowlist.json");

    /// <summary>The upstream vendors registry.</summary>
    public string Vendors => Path.Join(Checkout, "config", "upstream-skills.json");

    /// <summary>The ledger of skills activated beyond the allowlist, so they can be deactivated again.</summary>
    public string Ledger => Path.Join(Home, ".config", "devenv", "skill-activations.json");

    /// <summary>
    /// The sources searched, first seen winning: the working directory's <c>skills/</c> when it is not the canonical
    /// one, the canonical catalog, the user's <c>~/.config/devenv/skills</c>, then each vendor under
    /// <c>~/.config/devenv/vendors</c>. Only folders that exist are listed.
    /// </summary>
    public IReadOnlyList<SkillSource> Sources()
    {
        var sources = new List<SkillSource>();
        var workspace = Path.Join(WorkingDirectory, "skills");
        if (Directory.Exists(workspace)
            && !DirectoryLinks.Same(PathResolution.Resolve(workspace), PathResolution.Resolve(CanonicalSkills), Windows))
        {
            sources.Add(new("workspace", workspace));
        }

        if (Directory.Exists(CanonicalSkills))
        {
            sources.Add(new("shared-utilities", CanonicalSkills));
        }

        var custom = Path.Join(Home, ".config", "devenv", "skills");
        if (Directory.Exists(custom))
        {
            sources.Add(new("user-custom", custom));
        }

        var vendors = Path.Join(Home, ".config", "devenv", "vendors");
        if (Directory.Exists(vendors))
        {
            sources.AddRange(Directory.EnumerateDirectories(vendors)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !name.StartsWith('.'))
                .Order(PyText.CodePointOrder)
                .Select(name => new SkillSource(name, Path.Join(vendors, name))));
        }

        return sources;
    }
}

/// <summary>
/// Finds skills: every <c>SKILL.md</c> under each source, outside build output and dot folders, the first of each
/// name winning. An allowlist keeps the skills whose name or folder matches one of its globs; none, or one holding
/// <c>*</c>, keeps them all. Ported from <c>rig.skills.engine</c>.
/// </summary>
/// <param name="sources">The sources, in priority order.</param>
/// <param name="estateAllowlist">The estate allowlist, or null when there is none and every skill is curated.</param>
/// <param name="windows">Whether globs ignore case and slash direction, and paths sort ignoring case.</param>
public sealed class SkillCatalog(IReadOnlyList<SkillSource> sources, IReadOnlyList<string>? estateAllowlist, bool windows)
{
    /// <summary>Folders never searched: build output, dependencies, scratch space.</summary>
    public static readonly IReadOnlySet<string> Ignored = new HashSet<string>(StringComparer.Ordinal)
    {
        "node_modules", "dist", "build", "temp", ".git", "__pycache__",
    };

    private const string DefaultDescription = "Custom AI agent skill";

    /// <summary>The estate allowlist, or null when there is none.</summary>
    public IReadOnlyList<string>? EstateAllowlist => estateAllowlist;

    /// <summary>The catalog at <paramref name="places"/>, with its estate allowlist.</summary>
    public static SkillCatalog At(SkillPlaces places)
    {
        ArgumentNullException.ThrowIfNull(places);
        return new(places.Sources(), LoadNames(places.Allowlist), places.Windows);
    }

    /// <summary>A JSON list of strings, or null when the file is missing or is anything else.</summary>
    public static IReadOnlyList<string>? LoadNames(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllBytes(path)) is JsonArray items
                && items.All(i => i?.GetValueKind() == JsonValueKind.String)
                ? [.. items.Select(i => i!.GetValue<string>())]
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="name"/> matches <paramref name="allowlist"/>; null or one holding <c>*</c> matches all.</summary>
    public static bool IsAllowed(string name, IReadOnlyList<string>? allowlist, bool windows) =>
        allowlist is null || allowlist.Contains("*") || allowlist.Any(pattern => Glob.Matches(name, pattern, windows));

    /// <summary>Whether <paramref name="name"/> is in the estate allowlist.</summary>
    public bool IsCurated(string name) => IsAllowed(name, estateAllowlist, windows);

    /// <summary>The skills the estate allowlist keeps.</summary>
    public IReadOnlyList<Skill> Curated() => Discover(estateAllowlist);

    /// <summary>Every skill, allowlisted or not.</summary>
    public IReadOnlyList<Skill> Everything() => Discover(null);

    /// <summary>The skills <paramref name="allowlist"/> keeps.</summary>
    public IReadOnlyList<Skill> Discover(IReadOnlyList<string>? allowlist)
    {
        var skills = new List<Skill>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources.Where(s => Directory.Exists(s.Path)))
        {
            foreach (var skillMd in Find(source.Path, windows))
            {
                var folder = Path.GetDirectoryName(skillMd)!;
                var (name, description) = ReadFrontmatter(skillMd);
                if (seen.Contains(name)
                    || (!IsAllowed(name, allowlist, windows) && !IsAllowed(Path.GetFileName(folder), allowlist, windows)))
                {
                    continue;
                }

                skills.Add(new(name, description, PathResolution.Resolve(folder), VendorOf(skillMd) ?? source.Name));
                _ = seen.Add(name);
            }
        }

        return skills;
    }

    /// <summary>
    /// Every entry named <c>SKILL.md</c> under <paramref name="root"/>, sorted as Python sorts paths (part by part),
    /// never entering a linked folder, a dot folder or an <see cref="Ignored"/> one.
    /// </summary>
    public static IReadOnlyList<string> Find(string root, bool windows)
    {
        var found = new List<string>();
        Walk(root, windows, found);
        var order = Comparer<string>.Create((a, b) => CompareParts(Parts(a, windows), Parts(b, windows)));
        return [.. found.Order(order)];
    }

    /// <summary>A <c>SKILL.md</c>'s declared name and description, else its folder's name and a stock description.</summary>
    public static (string Name, string Description) ReadFrontmatter(string skillMd)
    {
        ArgumentNullException.ThrowIfNull(skillMd);
        var fallback = (Path.GetFileName(Path.GetDirectoryName(skillMd)) ?? string.Empty, DefaultDescription);
        if (!File.Exists(skillMd))
        {
            return fallback;
        }

        try
        {
            // Python's text mode: undecodable bytes replaced, a BOM kept, newlines translated.
            var text = new UTF8Encoding(false).GetString(File.ReadAllBytes(skillMd)).ReplaceLineEndings("\n");
            if (!text.StartsWith("---", StringComparison.Ordinal))
            {
                return fallback;
            }

            var close = text.IndexOf("---", 3, StringComparison.Ordinal);
            if (close < 0 || PyYaml.Load(text[3..close]) is not PyDict fields)
            {
                return fallback;
            }

            var name = Field(fields, "name") is { } n && PyText.Strip(n).Length > 0 ? n : fallback.Item1;
            var description = Field(fields, "description") is { } d && PyText.Strip(d).Length > 0 ? d : DefaultDescription;
            return (PyText.Strip(PyText.Strip(name), "'\""), CollapseWhitespace(description));
        }
        catch (Exception e) when (e is PyYamlException or IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    private static string? Field(PyDict fields, string key) =>
        fields.Entries.FirstOrDefault(e => e.Key is PyStr { Text: var k } && k == key).Value is PyStr { Text: var text } ? text : null;

    private static string CollapseWhitespace(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (!PyText.IsSpace(c))
            {
                _ = word.Append(c);
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                _ = word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return string.Join(' ', words);
    }

    // A vendored skill reports its vendor: the folder after the first "vendored" in its path.
    private static string? VendorOf(string skillMd)
    {
        var parts = Parts(skillMd, windows: false);
        var at = Array.IndexOf(parts, "vendored");
        return at >= 0 && at + 1 < parts.Length ? parts[at + 1] : null;
    }

    private static void Walk(string folder, bool windows, List<string> found)
    {
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.') || Ignored.Contains(name))
            {
                continue;
            }

            if (name.Equals("SKILL.md", comparison))
            {
                found.Add(entry);
            }

            if (Directory.Exists(entry) && !DirectoryLinks.IsLink(entry))
            {
                Walk(entry, windows, found);
            }
        }
    }

    private static string[] Parts(string path, bool windows) =>
        (windows ? path.ToLowerInvariant() : path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    private static int CompareParts(string[] a, string[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var order = PyText.CodePointOrder.Compare(a[i], b[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
