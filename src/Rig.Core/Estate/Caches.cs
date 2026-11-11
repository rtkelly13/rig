using System.Globalization;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>What one category's prune did, or would do.</summary>
public enum PruneStatus
{
    /// <summary>A dry run: the prune would run.</summary>
    WouldRun,

    /// <summary>The prune ran.</summary>
    Done,

    /// <summary>The tool, its daemon or its folder is not there.</summary>
    Skipped,

    /// <summary>The cache could not be located, or the prune failed.</summary>
    Failed,
}

/// <summary>One category's outcome.</summary>
/// <param name="Category">What the cache is, e.g. <c>uv cache</c>.</param>
/// <param name="Command">What prunes it.</param>
/// <param name="Status">What happened.</param>
/// <param name="Reason">The cache's folder, or why it was skipped or failed.</param>
/// <param name="SizeBefore">Bytes in the cache before pruning; null when it cannot be measured from here (Docker).</param>
/// <param name="Freed">Bytes freed; under a dry run, bytes that would be freed where that is knowable.</param>
public sealed record Pruned(string Category, string Command, PruneStatus Status, string Reason = "", long? SizeBefore = null, long? Freed = null)
{
    /// <summary>How the status prints: <c>would run</c>, <c>done</c>, <c>skipped</c> or <c>failed</c>.</summary>
    public string StatusName => Status switch
    {
        PruneStatus.WouldRun => "would run",
        PruneStatus.Done => "done",
        PruneStatus.Skipped => "skipped",
        _ => "failed",
    };
}

/// <summary>A cache whose tool can print where it lives and prune it.</summary>
/// <param name="Category">What the cache is.</param>
/// <param name="Tool">The program that owns it.</param>
/// <param name="Locate">The read-only arguments that print its folder, on the last line of output.</param>
/// <param name="Prune">The arguments that prune it.</param>
/// <param name="Prefix">Text before the path in the locate output, e.g. <c>http-cache:</c>.</param>
/// <param name="ClearsAll">The prune empties the cache, so a dry run knows exactly what it would free.</param>
public sealed record ToolCache(string Category, string Tool, IReadOnlyList<string> Locate, IReadOnlyList<string> Prune, string Prefix = "", bool ClearsAll = false)
{
    /// <summary>The prune as a command line.</summary>
    public string Command => string.Join(' ', [Tool, .. Prune]);
}

/// <summary>A tree's apparent size and the time of its newest file.</summary>
/// <param name="Bytes">Every file's length.</param>
/// <param name="Newest">The newest file's last write, or <see cref="DateTime.MinValue"/> for a tree with no files.</param>
public sealed record Footprint(long Bytes, DateTime Newest)
{
    /// <summary>
    /// Walks all of <paramref name="path"/>, <c>.git</c> included, summing apparent size and the newest write time.
    /// Ported from <c>src/rig/estate/disksize.py</c> <c>footprint</c>.
    /// </summary>
    /// <remarks>
    /// Never stops early and never reads link counts: it answers "how big is this, and is anyone still writing to it".
    /// Symlinks and junctions are not followed, and an entry or folder that cannot be read is passed over.
    /// </remarks>
    public static Footprint Of(string path)
    {
        long total = 0;
        var newest = DateTime.MinValue;
        var stack = new Stack<string>([path]);
        while (stack.TryPop(out var current))
        {
            foreach (var entry in TreeMeasure.Entries(current) ?? [])
            {
                try
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        stack.Push(entry.FullName);
                        continue;
                    }

                    total += ((FileInfo)entry).Length;
                    newest = entry.LastWriteTimeUtc > newest ? entry.LastWriteTimeUtc : newest;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Gone or unreadable since the listing; Python's walk skips it too.
                }
            }
        }

        return new Footprint(total, newest);
    }
}

/// <summary>A Claude Code session folder nothing has written to for the idle period.</summary>
/// <param name="Path">The session folder.</param>
/// <param name="Bytes">Its apparent size.</param>
/// <param name="LastWrite">The newest file's write, or the folder's own when that is newer.</param>
public sealed record IdleSession(string Path, long Bytes, DateTime LastWrite);


/// <summary>
/// Caches that refill themselves: prune them, and say how much each one gave back. Ported from
/// <c>src/rig/estate/caches.py</c>.
/// </summary>
/// <remarks>
/// Five categories, each owned by its own tool: uv's cache, pnpm's store, NuGet's http-cache, Claude Code's idle session
/// folders and Docker's build cache. Every tool is optional: one that cannot be found is skipped with that reason, and so
/// is Docker when its daemon does not answer. A dry run runs only the read-only commands that locate each cache.
/// </remarks>
/// <param name="proc">Runs each tool.</param>
/// <param name="findTool">Finds a tool's executable, or null; <see cref="FindTool"/> outside tests.</param>
/// <param name="removal">Deletes idle Claude session folders.</param>
/// <param name="time">The clock a session's idleness is measured by.</param>
public sealed partial class Caches(IProc proc, Func<string, string?> findTool, TreeRemoval removal, TimeProvider time)
{
    /// <summary>A read-only locate or probe.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A prune.</summary>
    public static readonly TimeSpan PruneTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Docker build cache older than a week goes.</summary>
    public const string DockerFilter = "until=168h";

    /// <summary>uv's cache.</summary>
    public static readonly ToolCache Uv = new("uv cache", "uv", ["cache", "dir"], ["cache", "prune"]);

    /// <summary>pnpm's content-addressed store.</summary>
    public static readonly ToolCache Pnpm = new("pnpm store", "pnpm", ["store", "path"], ["store", "prune"]);

    /// <summary>NuGet's http-cache, which the clear empties.</summary>
    public static readonly ToolCache NuGet = new(
        "nuget http-cache",
        "dotnet",
        ["nuget", "locals", "http-cache", "--list"],
        ["nuget", "locals", "http-cache", "--clear"],
        Prefix: "http-cache:",
        ClearsAll: true);

    /// <summary>The tool caches, in the order they are pruned.</summary>
    public static readonly IReadOnlyList<ToolCache> ToolCaches = [Uv, Pnpm, NuGet];

    private static readonly Dictionary<string, long> DockerUnits = new(StringComparer.Ordinal)
    {
        ["B"] = 1, ["KB"] = 1000, ["MB"] = 1000L * 1000, ["GB"] = 1000L * 1000 * 1000, ["TB"] = 1000L * 1000 * 1000 * 1000,
    };

    /// <summary>Where Claude Code keeps per-session scratch: <c>&lt;temp&gt;/claude/&lt;project&gt;/&lt;session-id&gt;</c>.</summary>
    public static string DefaultClaudeTemp() => Path.Join(Path.GetTempPath(), "claude");

    /// <summary>
    /// <paramref name="name"/> on <c>PATH</c>, else in its installer's usual folder: a scheduled task starts with a thinner
    /// <c>PATH</c> than a terminal. Null when neither has it.
    /// </summary>
    public static string? FindTool(string name) =>
        ProgramPath.Find(name)
        ?? ProgramPath.Find(
            name,
            string.Join(Path.PathSeparator, InstallDirs()),
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATHEXT") : null);

    /// <summary>Where the installers of these tools put them.</summary>
    public static IReadOnlyList<string> InstallDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Join(home, "AppData", "Local");
        var roaming = Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } r ? r : Path.Join(home, "AppData", "Roaming");
        var programFiles = Environment.GetEnvironmentVariable("PROGRAMFILES") is { Length: > 0 } p ? p : @"C:\Program Files";
        return
        [
            Path.Join(home, ".local", "bin"),
            Path.Join(home, ".cargo", "bin"),
            Path.Join(local, "Microsoft", "WinGet", "Links"),
            Path.Join(local, "pnpm"),
            Path.Join(roaming, "npm"),
            Path.Join(programFiles, "dotnet"),
            Path.Join(programFiles, "Docker", "Docker", "resources", "bin"),
        ];
    }

    /// <summary>Every category, in a fixed order, each independent of the others.</summary>
    public async Task<IReadOnlyList<Pruned>> PruneAllAsync(bool apply, string claudeTemp, int idleDays, CancellationToken cancellationToken = default)
    {
        var results = new List<Pruned>();
        foreach (var cache in ToolCaches)
        {
            results.Add(await PruneToolCacheAsync(cache, apply, cancellationToken).ConfigureAwait(false));
        }

        results.Add(await PruneClaudeTempAsync(claudeTemp, apply, idleDays, cancellationToken).ConfigureAwait(false));
        results.Add(await PruneDockerAsync(apply, cancellationToken).ConfigureAwait(false));
        return results;
    }

    /// <summary>Locates <paramref name="cache"/>, measures it, and with <paramref name="apply"/> prunes it and measures again.</summary>
    public async Task<Pruned> PruneToolCacheAsync(ToolCache cache, bool apply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (findTool(cache.Tool) is not { } exe)
        {
            return new Pruned(cache.Category, cache.Command, PruneStatus.Skipped, $"{cache.Tool} not found on PATH");
        }

        var located = await RunAsync([exe, .. cache.Locate], ProbeTimeout, cancellationToken).ConfigureAwait(false);
        var last = LastLine(located.Out);
        var where = (last.StartsWith(cache.Prefix, StringComparison.Ordinal) ? last[cache.Prefix.Length..] : last).Trim();
        if (located.Code != 0 || where.Length == 0)
        {
            return new Pruned(cache.Category, cache.Command, PruneStatus.Failed, $"cannot locate the cache: {Failure(located)}");
        }

        var before = Size(where);
        if (!apply)
        {
            return new Pruned(cache.Category, cache.Command, PruneStatus.WouldRun, where, before, cache.ClearsAll ? before : null);
        }

        var done = await RunAsync([exe, .. cache.Prune], PruneTimeout, cancellationToken).ConfigureAwait(false);
        return done.Code != 0
            ? new Pruned(cache.Category, cache.Command, PruneStatus.Failed, Failure(done), before)
            : new Pruned(cache.Category, cache.Command, PruneStatus.Done, where, before, Math.Max(before - Size(where), 0));
    }

    /// <summary>Build cache older than a week, when Docker is installed and its daemon answers.</summary>
    public async Task<Pruned> PruneDockerAsync(bool apply, CancellationToken cancellationToken = default)
    {
        const string category = "docker build cache";
        const string command = "docker builder prune -f --filter " + DockerFilter;
        if (findTool("docker") is not { } exe)
        {
            return new Pruned(category, command, PruneStatus.Skipped, "docker not found on PATH");
        }

        var info = await RunAsync([exe, "info", "--format", "{{.ServerVersion}}"], ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (info.Code != 0)
        {
            return new Pruned(category, command, PruneStatus.Skipped, "Docker daemon not running");
        }

        if (!apply)
        {
            return new Pruned(category, command, PruneStatus.WouldRun, "size is known only after the prune");
        }

        var done = await RunAsync([exe, "builder", "prune", "-f", "--filter", DockerFilter], PruneTimeout, cancellationToken).ConfigureAwait(false);
        return done.Code != 0
            ? new Pruned(category, command, PruneStatus.Failed, Failure(done))
            : new Pruned(category, command, PruneStatus.Done, Freed: DockerReclaimed(done.Out) ?? 0);
    }

    /// <summary>Bytes from the <c>Total:</c> line <c>docker builder prune</c> ends with, in decimal units; null when absent.</summary>
    public static long? DockerReclaimed(string output)
    {
        var match = DockerTotal().Match(output);
        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var unit = match.Groups[2].Value.ToUpperInvariant().Replace("I", string.Empty, StringComparison.Ordinal);
        return (long)(value * DockerUnits.GetValueOrDefault(unit, 1));
    }

    /// <summary>
    /// Session folders under <paramref name="root"/> (<c>&lt;project&gt;/&lt;session-id&gt;</c>) nothing has written to for
    /// <paramref name="idleDays"/> days. The folder's own write time counts, so a session that has only just created its
    /// empty scratchpad is not idle.
    /// </summary>
    public IReadOnlyList<IdleSession> IdleSessions(string root, int idleDays)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-idleDays);
        var idle = new List<IdleSession>();
        foreach (var project in Subdirs(root))
        {
            foreach (var session in Subdirs(project.FullName))
            {
                if (!SessionName().IsMatch(session.Name))
                {
                    continue;
                }

                var size = Footprint.Of(session.FullName);
                DateTime own;
                try
                {
                    session.Refresh();
                    own = session.LastWriteTimeUtc;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var last = size.Newest > own ? size.Newest : own;
                if (last <= cutoff)
                {
                    idle.Add(new IdleSession(session.FullName, size.Bytes, last));
                }
            }
        }

        return idle;
    }

    /// <summary>Deletes the Claude Code session folders idle <paramref name="idleDays"/> or more.</summary>
    public async Task<Pruned> PruneClaudeTempAsync(string root, bool apply, int idleDays, CancellationToken cancellationToken = default)
    {
        const string category = "claude temp";
        var command = $"delete session dirs idle {idleDays}+ days under {root}";
        if (!Directory.Exists(root))
        {
            return new Pruned(category, command, PruneStatus.Skipped, $"{root} does not exist");
        }

        var sessions = IdleSessions(root, idleDays);
        var total = sessions.Sum(s => s.Bytes);
        if (!apply)
        {
            return new Pruned(category, command, PruneStatus.WouldRun, $"{sessions.Count} idle session dir(s)", total, total);
        }

        long freed = 0;
        var failures = new List<string>();
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await removal.RemoveAsync(session.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(session.Path)}: {e.Message}");
                continue;
            }

            freed += session.Bytes;
        }

        return failures.Count > 0
            ? new Pruned(category, command, PruneStatus.Failed, $"{failures.Count} of {sessions.Count} not removed; first: {failures[0]}", total, freed)
            : new Pruned(category, command, PruneStatus.Done, $"{sessions.Count} session dir(s) removed", total, freed);
    }

    private Task<Ran> RunAsync(string[] argv, TimeSpan timeout, CancellationToken cancellationToken) =>
        proc.RunAsync(new ProcRequest(argv) { Timeout = timeout }, cancellationToken);

    private static long Size(string folder) => Directory.Exists(folder) ? Footprint.Of(folder).Bytes : 0;

    private static string LastLine(string text) =>
        text.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) ?? string.Empty;

    private static string Failure(Ran ran) =>
        LastLine(ran.Err) is { Length: > 0 } err ? err
        : LastLine(ran.Out) is { Length: > 0 } output ? output
        : $"exit {ran.Code}";

    private static IEnumerable<DirectoryInfo> Subdirs(string folder) =>
        (TreeMeasure.Entries(folder) ?? [])
            .OfType<DirectoryInfo>()
            .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderBy(d => d.Name, StringComparer.Ordinal);

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SessionName();

    [GeneratedRegex(@"^Total(?: reclaimed space)?:\s*([0-9.]+)\s*([kKMGT]?i?B)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DockerTotal();
}
