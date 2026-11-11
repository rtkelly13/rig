using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;
using Rig.Core.Processes;

namespace Rig.Core.Skills;

/// <summary>An upstream repository whose skills are vendored into the catalog.</summary>
/// <param name="Name">The vendor's name: the folder under <c>skills/vendored</c>.</param>
/// <param name="Remote">What it is cloned from: a git URL, HTTPS or SSH.</param>
/// <param name="FolderName">The clone's folder name under the external clones folder; empty means <paramref name="Name"/>.</param>
/// <param name="Branch">The branch tracked.</param>
/// <param name="SkillsSubdir">Where in the repository its skills live; the whole repository when that is missing.</param>
/// <param name="Allowlist">The skills to take, overriding <paramref name="Include"/>; null when not set.</param>
/// <param name="Include">The skills to take when there is no allowlist.</param>
/// <param name="Exclude">The skills never taken.</param>
public sealed record SkillVendor(
    string Name,
    string Remote,
    string FolderName,
    string Branch,
    string SkillsSubdir,
    IReadOnlyList<string>? Allowlist,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude)
{
    /// <summary>The clone's folder name.</summary>
    public string CloneFolder => FolderName.Length > 0 ? FolderName : Name;

    /// <summary>Whether the skill folder <paramref name="name"/> is taken: not excluded, and allowed (none or <c>*</c> allows all).</summary>
    public bool IsIncluded(string name, bool windows)
    {
        if (Exclude.Any(pattern => Glob.Matches(name, pattern, windows)))
        {
            return false;
        }

        var allowed = Allowlist ?? Include;
        return allowed.Count == 0 || allowed.Contains("*") || allowed.Any(pattern => Glob.Matches(name, pattern, windows));
    }
}

/// <summary>
/// Vendors upstream skills: clones or updates each vendor's repository, then copies the skills it allows into
/// <c>skills/vendored/&lt;vendor&gt;</c>, removing those it no longer does. Ported from <c>rig.skills.vendor</c>.
/// Clones live in the code root's <c>external/</c>, so every git call on them is hardened.
/// </summary>
/// <param name="git">Runs git.</param>
/// <param name="clones">The folder clones live in.</param>
/// <param name="vendored">The folder skills are extracted to.</param>
/// <param name="windows">Whether skill globs ignore case.</param>
public sealed class SkillVendors(Git git, string clones, string vendored, bool windows)
{
    /// <summary>A clone or fetch of a whole repository takes longer than an ordinary git call.</summary>
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The vendors used when the registry is missing or empty.</summary>
    public static IReadOnlyList<SkillVendor> Defaults { get; } =
    [
        new("mattpocock-skills", "https://github.com/mattpocock/skills.git", "skills", "main", "skills", null, ["*"], []),
        new("pstack", "https://github.com/backnotprop/pstack.git", "pstack", "main", "skills", null, ["*"], []),
        new("visual-explainer", "https://github.com/nicobailon/visual-explainer.git", "visual-explainer", "main", "plugins", null, ["visual-explainer"], []),
    ];

    private static readonly HashSet<string> NotCopied = new HashSet<string>(StringComparer.Ordinal)
    {
        "node_modules", ".git", "dist", "build", "temp", "__pycache__",
    };

    /// <summary>The vendors in the registry at <paramref name="path"/>, or <see cref="Defaults"/> when it is missing or empty.</summary>
    /// <exception cref="InvalidDataException">The registry is not a list of valid vendors.</exception>
    public static IReadOnlyList<SkillVendor> Load(string path)
    {
        if (!File.Exists(path))
        {
            return Defaults;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllBytes(path));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Schema validation error in {path}: {e.Message}", e);
        }

        if (root is not JsonArray items)
        {
            throw new InvalidDataException($"Schema validation error in {path}: the registry must be a list.");
        }

        var vendors = items.Select((item, i) => Vendor(item, $"{path}[{i}]")).ToList();
        return vendors.Count > 0 ? vendors : Defaults;
    }

    /// <summary>Clones <paramref name="vendor"/>, or brings its clone up to date, and returns the clone's folder.</summary>
    /// <exception cref="IOException">The clone failed.</exception>
    public async Task<string> CloneOrUpdateAsync(SkillVendor vendor, Action<string> say, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(say);
        _ = Directory.CreateDirectory(clones);
        var repo = Path.Join(clones, vendor.CloneFolder);
        if (Directory.Exists(Path.Join(repo, ".git")))
        {
            say($"Updating {vendor.Name} from {vendor.Remote}...");
            var pulled = await git.RunAsync(repo, ["pull", "--ff-only"], NetworkTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!pulled.Ok)
            {
                say($"Fast-forward failed, fetching origin/{vendor.Branch}...");
                var fetched = await git.RunAsync(repo, ["fetch", "origin", vendor.Branch], NetworkTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!fetched.Ok)
                {
                    throw new IOException($"Git fetch failed for {vendor.Name}: {fetched.Err.Trim()}");
                }

                var reset = await git.RunAsync(repo, ["reset", "--hard", $"origin/{vendor.Branch}"], cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!reset.Ok)
                {
                    throw new IOException($"Git reset failed for {vendor.Name}: {reset.Err.Trim()}");
                }
            }

            say($"✓ Updated {vendor.Name}");
            return repo;
        }

        say($"Cloning {vendor.Name} from {vendor.Remote}...");
        var cloned = await git.RunAsync(
            clones, ["clone", "--depth", "1", "--branch", vendor.Branch, "--", vendor.Remote, repo], NetworkTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!cloned.Ok)
        {
            throw new IOException($"Git clone failed for {vendor.Name}: {cloned.Err.Trim()}");
        }

        say($"✓ Cloned {vendor.Name}");
        return repo;
    }

    /// <summary>
    /// Copies the skills <paramref name="vendor"/> allows from its clone at <paramref name="repo"/> into its vendored
    /// folder, replacing earlier copies and removing skills it no longer allows. Returns the skills copied.
    /// </summary>
    public IReadOnlyList<string> Extract(SkillVendor vendor, string repo)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        var destination = Path.Join(vendored, vendor.Name);
        _ = Directory.CreateDirectory(destination);
        var root = Path.Join(repo, vendor.SkillsSubdir);
        if (!Directory.Exists(root))
        {
            root = repo;
        }

        var extracted = new List<string>();
        foreach (var skillMd in SkillCatalog.Find(root, windows))
        {
            var source = Path.GetDirectoryName(skillMd)!;
            var name = Path.GetFileName(source);
            if (!vendor.IsIncluded(name, windows))
            {
                continue;
            }

            var target = Path.Join(destination, name);
            if (DirectoryLinks.Occupied(target))
            {
                DirectoryLinks.Remove(target);
            }

            DirectoryLinks.Copy(source, target, n => NotCopied.Contains(n) || n.StartsWith('.'));
            extracted.Add(name);
        }

        foreach (var stale in Directory.EnumerateDirectories(destination).Where(d => !extracted.Contains(Path.GetFileName(d))).ToList())
        {
            DirectoryLinks.Remove(stale);
        }

        return extracted;
    }

    /// <summary>Clones or updates each of <paramref name="vendors"/> and extracts its skills; returns each vendor's skills.</summary>
    public async Task<IReadOnlyList<(string Vendor, IReadOnlyList<string> Skills)>> UpdateAsync(
        IReadOnlyList<SkillVendor> vendors, Action<string> say, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vendors);
        ArgumentNullException.ThrowIfNull(say);
        var results = new List<(string, IReadOnlyList<string>)>();
        foreach (var vendor in vendors)
        {
            var repo = await CloneOrUpdateAsync(vendor, say, cancellationToken).ConfigureAwait(false);
            var skills = Extract(vendor, repo);
            results.Add((vendor.Name, skills));
            say($"✓ Extracted {skills.Count} skills from {vendor.Name}");
        }

        return results;
    }

    private static SkillVendor Vendor(JsonNode? node, string at)
    {
        if (node is not JsonObject fields)
        {
            throw Invalid(at, "a vendor must be an object");
        }

        var name = Text(fields, "name", at, required: true)!;
        var folder = Text(fields, "folder_name", at) ?? string.Empty;
        var url = Text(fields, "url", at, required: true)!;
        var branch = Text(fields, "branch", at) ?? "main";
        var subdir = Text(fields, "skills_subdir", at) ?? "skills";
        // Each becomes a path or a git argument: none may leave its folder or read as an option.
        if (!SkillCatalog.IsPlainName(name))
        {
            throw Invalid(at, "name must be a single folder name");
        }

        if (folder.Length > 0 && !SkillCatalog.IsPlainName(folder))
        {
            throw Invalid(at, "folder_name must be a single folder name");
        }

        if (Path.IsPathRooted(subdir) || subdir.Split('/', '\\').Contains(".."))
        {
            throw Invalid(at, "skills_subdir must stay inside the repository");
        }

        if (url.StartsWith('-') || branch.StartsWith('-'))
        {
            throw Invalid(at, "url and branch must not start with '-'");
        }

        return new(
            name,
            url,
            folder,
            branch,
            subdir,
            fields["allowlist"] is null ? null : Names(fields, "allowlist", at),
            fields.ContainsKey("include") ? Names(fields, "include", at) : ["*"],
            fields.ContainsKey("exclude") ? Names(fields, "exclude", at) : []);
    }

    private static string? Text(JsonObject fields, string key, string at, bool required = false)
    {
        if (!fields.TryGetPropertyValue(key, out var value))
        {
            return required ? throw Invalid(at, $"{key} is required") : null;
        }

        if (value?.GetValueKind() != JsonValueKind.String)
        {
            throw Invalid(at, $"{key} must be a string");
        }

        var text = value.GetValue<string>();
        return required && text.Length == 0 ? throw Invalid(at, $"{key} must not be empty") : text;
    }

    private static IReadOnlyList<string> Names(JsonObject fields, string key, string at) =>
        fields[key] is JsonArray items && items.All(i => i?.GetValueKind() == JsonValueKind.String)
            ? [.. items.Select(i => i!.GetValue<string>())]
            : throw Invalid(at, $"{key} must be a list of strings");

    private static InvalidDataException Invalid(string at, string problem) => new($"Schema validation error in {at}: {problem}.");
}
