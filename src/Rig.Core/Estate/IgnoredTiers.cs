using Rig.Core.Storage;

namespace Rig.Core.Estate;

/// <summary>
/// What an ignored path is, in cleanup order: the lower the tier, the sooner deleting it is safe and the more often it is
/// worth doing. <c>obj</c> and <c>bin</c> churn on every build; a <c>TODO.md</c> is someone's notes.
/// </summary>
public enum IgnoredTier
{
    /// <summary>Not classified: never read as a tier, so an unset value is not the most deletable one.</summary>
    None = 0,

    /// <summary>Compiler and bundler output: rebuilt by the next build.</summary>
    BuildOutput = 1,

    /// <summary>Test, lint and interpreter caches: refilled by the next run.</summary>
    ToolCache = 2,

    /// <summary>Restored packages and environments: back after an install, at the cost of a download.</summary>
    Dependencies = 3,

    /// <summary><c>temp/</c>: disposable, but may hold nested checkouts and hand-written scripts.</summary>
    Scratch = 4,

    /// <summary>Ignored Markdown: someone's notes. Carried into a shelf, never deleted.</summary>
    Notes = 5,

    /// <summary>Environment files and local settings: never deleted automatically, never pushed.</summary>
    LocalConfig = 6,

    /// <summary>Nothing in the table: a person decides.</summary>
    Unknown = 7,
}

/// <summary>How an ignored path was classified.</summary>
/// <param name="Tier">Its cleanup tier.</param>
/// <param name="Category">A label for reports: <c>dotnet build output</c>, <c>node_modules</c>.</param>
public sealed record IgnoredClass(IgnoredTier Tier, string Category)
{
    /// <summary>Unmatched.</summary>
    public static readonly IgnoredClass Unknown = new(IgnoredTier.Unknown, "unknown");

    /// <summary>Comes back from a command: build output, a tool cache or dependencies.</summary>
    public bool Regenerates => Tier is IgnoredTier.BuildOutput or IgnoredTier.ToolCache or IgnoredTier.Dependencies;
}

/// <summary>One row of the table.</summary>
/// <param name="Name">A folder or file name, or a suffix starting with <c>.</c> when <paramref name="Suffix"/>.</param>
/// <param name="Class">What it is.</param>
/// <param name="Folder">Matches a wholly ignored folder; otherwise a file.</param>
/// <param name="Beside">On disk, a file matching one of these must sit beside the folder: <c>bin</c> is build output only next to a project.</param>
/// <param name="Contents">Everything below the folder is generated too, so a file inside it classifies as the folder.</param>
/// <param name="Trimmed">Idle-worktree trimming may take it (<see cref="Trim"/>).</param>
/// <param name="Suffix">Matches any path part ending in <paramref name="Name"/>.</param>
public sealed record IgnoredKind(
    string Name, IgnoredClass Class, bool Folder = true, IReadOnlyList<string>? Beside = null, bool Contents = false, bool Trimmed = false, bool Suffix = false);

/// <summary>
/// The one table of ignored names: what idle-worktree trimming takes, what shelving may drop, and what a worktree
/// inventory reports. Classification is by name, platform-neutral; only the bytes behind it differ by OS.
/// </summary>
/// <remarks>
/// Two readings. <see cref="Of"/> classifies a path as <c>git ls-files --ignored --directory</c> lists it, by names
/// alone: a trailing <c>/</c> marks a wholly ignored folder. <see cref="OnDisk"/> classifies a folder that exists, and
/// applies <see cref="IgnoredKind.Beside"/>: a <c>bin</c> holding hand-written scripts is not build output.
/// </remarks>
public static class IgnoredTiers
{
    private static readonly IgnoredClass DotnetBuild = new(IgnoredTier.BuildOutput, "dotnet build output");
    private static readonly IgnoredClass RustBuild = new(IgnoredTier.BuildOutput, "rust build output");
    private static readonly IgnoredClass JsBuild = new(IgnoredTier.BuildOutput, "js build output");
    private static readonly IgnoredClass OtherBuild = new(IgnoredTier.BuildOutput, "build output");
    private static readonly IgnoredClass ToolCaches = new(IgnoredTier.ToolCache, "tool caches");
    private static readonly IgnoredClass Litter = new(IgnoredTier.ToolCache, "os litter");
    private static readonly IgnoredClass NodeModules = new(IgnoredTier.Dependencies, "node_modules");
    private static readonly IgnoredClass Venv = new(IgnoredTier.Dependencies, "python venv");
    private static readonly IgnoredClass DotnetPackages = new(IgnoredTier.Dependencies, "dotnet packages");
    private static readonly IgnoredClass OtherDependencies = new(IgnoredTier.Dependencies, "dependencies");
    private static readonly IgnoredClass Scratch = new(IgnoredTier.Scratch, "scratch");
    private static readonly IgnoredClass Notes = new(IgnoredTier.Notes, "notes");
    private static readonly IgnoredClass LocalConfig = new(IgnoredTier.LocalConfig, "local config");

    private static readonly string[] DotnetProjects = ["*.csproj", "*.fsproj", "*.vbproj"];

    /// <summary>Every row, in cleanup order.</summary>
    public static readonly IReadOnlyList<IgnoredKind> Table =
    [
        new("bin", DotnetBuild, Beside: DotnetProjects, Trimmed: true),
        new("obj", DotnetBuild, Beside: DotnetProjects, Trimmed: true),
        new("target", RustBuild, Beside: ["Cargo.toml"], Trimmed: true),
        new("dist", JsBuild, Trimmed: true),
        new(".next", JsBuild, Contents: true, Trimmed: true),
        new(".turbo", JsBuild, Contents: true, Trimmed: true),
        new("storybook-static", JsBuild, Trimmed: true),
        new("build", OtherBuild),
        new("TestResults", OtherBuild, Contents: true),
        new(".tsbuildinfo", OtherBuild, Folder: false, Suffix: true),
        new(".feature.cs", OtherBuild, Folder: false, Suffix: true),
        new(".egg-info", OtherBuild, Folder: false, Suffix: true),
        new(".pytest_cache", ToolCaches, Contents: true, Trimmed: true),
        new(".mypy_cache", ToolCaches, Contents: true, Trimmed: true),
        new(".ruff_cache", ToolCaches, Contents: true, Trimmed: true),
        new(".tox", ToolCaches, Trimmed: true),
        new(".nox", ToolCaches, Trimmed: true),
        new("__pycache__", ToolCaches, Contents: true),
        new("coverage", ToolCaches),
        new("logs", ToolCaches),
        new(".pyc", ToolCaches, Folder: false, Suffix: true),
        new(".log", ToolCaches, Folder: false, Suffix: true),
        new(".DS_Store", Litter, Folder: false),
        new("Thumbs.db", Litter, Folder: false),
        new("node_modules", NodeModules, Contents: true, Trimmed: true),
        new(".venv", Venv, Contents: true, Trimmed: true),
        new("venv", Venv, Contents: true, Trimmed: true),
        new("packages", DotnetPackages, Beside: ["*.sln"], Trimmed: true),
        new("paket-files", OtherDependencies, Contents: true),
        new(".wireit", OtherDependencies, Contents: true),
    ];

    // Husky's generated hook shims: a folder named `_`, which is too common a name to match alone.
    private const string HuskyShims = ".husky/_";

    private static readonly Dictionary<string, IgnoredKind> Folders =
        Table.Where(kind => kind.Folder).ToDictionary(kind => kind.Name, StringComparer.Ordinal);

    private static readonly Dictionary<string, IgnoredKind> Files =
        Table.Where(kind => !kind.Folder && !kind.Suffix).ToDictionary(kind => kind.Name, StringComparer.Ordinal);

    private static readonly IgnoredKind[] Suffixes = [.. Table.Where(kind => kind.Suffix)];

    /// <summary>
    /// Classifies an ignored path by name, as <c>git ls-files --ignored --directory</c> lists it relative to the
    /// worktree. A wholly ignored folder (trailing <c>/</c>) counts by its own name; a file by its name or suffix, or
    /// as its folder when it sits under one whose <see cref="IgnoredKind.Contents"/> are all generated:
    /// <c>node_modules/a/x.js</c> is dependencies, <c>packages/api/.env</c> is local config.
    /// </summary>
    public static IgnoredClass Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normal = path.Replace('\\', '/');
        var parts = normal.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return IgnoredClass.Unknown;
        }

        var last = parts[^1];
        var folder = normal.EndsWith('/', StringComparison.Ordinal);
        var joined = $"/{string.Join('/', parts)}/";
        return parts[..^1].Select(part => Folders.GetValueOrDefault(part)).FirstOrDefault(kind => kind is { Contents: true })?.Class
            ?? (joined.Contains($"/{HuskyShims}/", StringComparison.Ordinal) ? OtherBuild : null)
            ?? Suffixes.FirstOrDefault(kind => parts.Any(part => part.EndsWith(kind.Name, StringComparison.Ordinal)))?.Class
            ?? (folder ? Folders.GetValueOrDefault(last)?.Class : null)
            ?? Files.GetValueOrDefault(last)?.Class
            ?? Personal(parts, folder);
    }

    /// <summary>
    /// The trimmable class of a folder that exists, or null: its name must be a <see cref="IgnoredKind.Trimmed"/> row,
    /// and a row with <see cref="IgnoredKind.Beside"/> needs a matching file next to it.
    /// </summary>
    public static IgnoredClass? OnDisk(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var parent = Path.GetDirectoryName(trimmed) ?? string.Empty;
        return Folders.GetValueOrDefault(Path.GetFileName(trimmed)) is { Trimmed: true } kind
            && (kind.Beside is not { } beside || beside.Any(pattern => Has(parent, pattern)))
            ? kind.Class
            : null;
    }

    // The tiers no command regenerates: scratch, notes, and settings that hold secrets.
    private static IgnoredClass Personal(string[] parts, bool folder)
    {
        var last = parts[^1];
        if (parts[0] == "temp")
        {
            return Scratch;
        }

        if (!folder && (last == ".env" || last.StartsWith(".env.", StringComparison.Ordinal) || last.EndsWith(".local", StringComparison.Ordinal)
            || last.Contains(".local.", StringComparison.Ordinal)))
        {
            return LocalConfig;
        }

        return !folder && last.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? Notes : IgnoredClass.Unknown;
    }

    private static bool Has(string folder, string pattern) =>
        FileGuard.Try(() => Directory.EnumerateFiles(folder, pattern).Any(), false);
}
