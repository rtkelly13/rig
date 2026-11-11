using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>One folder in a WizTree export: its path with no trailing separator, and what it holds.</summary>
/// <param name="Path">The folder; the drive root is <c>C:</c>.</param>
/// <param name="Allocated">Bytes allocated on disk, the number that matters: hardlinks make logical size overstate.</param>
/// <param name="Size">Logical bytes.</param>
/// <param name="Files">Files beneath it.</param>
public sealed record Folder(string Path, long Allocated, long Size, long Files);

/// <summary>One folder in the drill-down, <paramref name="Level"/> segments below the drive root.</summary>
public sealed record TreeRow(int Level, Folder Folder);

/// <summary>A known Windows disk consumer and what to do about it.</summary>
/// <param name="Label">What it is.</param>
/// <param name="Pattern">Its path: <c>%VAR%</c> expands from the environment and <c>*</c> matches within one segment.</param>
/// <param name="Remedy">What frees it.</param>
/// <param name="Safety"><c>safe</c> regenerates; <c>review</c> needs a human look; <c>never</c> is not to be deleted by hand.</param>
public sealed record Hog(string Label, string Pattern, string Remedy, string Safety);

/// <summary>A catalogued hog found on the volume: its allocated total and the folders that matched.</summary>
public sealed record HogHit(Hog Hog, long Allocated, IReadOnlyList<string> Paths);

/// <summary>
/// Where a whole Windows volume's space went, from a WizTree MFT export. Ported from <c>src/rig/estate/volume.py</c>.
/// Walking ten million files takes most of an hour; WizTree reads the Master File Table and exports every folder's
/// allocated size in under a minute. Three views: <see cref="Tree"/> (where is it), <see cref="Partition"/> (what kind
/// of thing, each subtree counted once) and <see cref="Hogs"/> (the known offenders, each with its remedy).
/// </summary>
public static class Volume
{
    /// <summary>Where winget installs WizTree.</summary>
    public const string WizTree = @"C:\Program Files\WizTree\WizTree64.exe";

    /// <summary>The known Windows offenders.</summary>
    public static readonly IReadOnlyList<Hog> Catalogue =
    [
        new("Docker Desktop disk", @"%LOCALAPPDATA%\Docker\wsl", "docker system prune -a, then compact the VHDX (prune alone frees nothing)", "review"),
        new("WSL distro disks", @"%LOCALAPPDATA%\Packages\*CanonicalGroupLimited*", "wsl --shutdown; wsl --manage <distro> --set-sparse true", "review"),
        new("WSL disks (new layout)", @"%LOCALAPPDATA%\wsl", "wsl --shutdown; compact or --set-sparse", "review"),
        new("JumpCloud ADMU backups", @"%LOCALAPPDATA%\JumpCloudADMU", "delete *_permission_backup_* (keep the csv/log beside them)", "review"),
        new("uv cache", @"%LOCALAPPDATA%\uv\cache", "uv cache prune (only frees entries no .venv links to)", "safe"),
        new("pnpm store", @"%LOCALAPPDATA%\pnpm\store", "pnpm store prune", "safe"),
        new("npm cache", @"%LOCALAPPDATA%\npm-cache", "npm cache clean --force", "safe"),
        new("pip cache", @"%LOCALAPPDATA%\pip\cache", "pip cache purge", "safe"),
        new("NuGet global packages", @"%USERPROFILE%\.nuget\packages", "dotnet nuget locals all --clear", "safe"),
        new("NuGet HTTP cache", @"%LOCALAPPDATA%\NuGet", "dotnet nuget locals all --clear", "safe"),
        new("Cargo registry", @"%USERPROFILE%\.cargo\registry", "cargo cache --autoclean (cargo-cache)", "safe"),
        new("Playwright browsers", @"%LOCALAPPDATA%\ms-playwright", "npx playwright uninstall --all; reinstall what is pinned", "safe"),
        new("Hugging Face cache", @"%USERPROFILE%\.cache\huggingface", "huggingface-cli delete-cache", "review"),
        new("User temp", @"%LOCALAPPDATA%\Temp", "delete entries older than a day", "safe"),
        new("Windows temp", @"C:\Windows\Temp", "delete entries older than a day (admin)", "safe"),
        new("Windows Update downloads", @"C:\Windows\SoftwareDistribution\Download", "stop wuauserv, clear, start (admin)", "safe"),
        new("Component store", @"C:\Windows\WinSxS", "Dism /online /Cleanup-Image /StartComponentCleanup", "never"),
        new("Installer cache", @"C:\Windows\Installer", "never by hand; PatchCleaner if anything", "never"),
        new("Recycle Bin", @"C:\$Recycle.Bin", "Clear-RecycleBin", "safe"),
        new("Downloads", @"%USERPROFILE%\Downloads", "estate downloads", "review"),
        new("Claude Code projects", @"%USERPROFILE%\.claude\projects", "old session transcripts", "review"),
        new("Claude desktop", @"%APPDATA%\Claude", "VM bundles and caches", "review"),
        new("VS Code extensions", @"%USERPROFILE%\.vscode\extensions", "uninstall unused; old versions linger", "review"),
        new("JetBrains caches", @"%LOCALAPPDATA%\JetBrains", "File > Invalidate Caches; remove old IDE versions", "review"),
        new("Chrome profile", @"%LOCALAPPDATA%\Google\Chrome\User Data", "clear browsing data", "review"),
        new("Box Drive", @"%USERPROFILE%\Box", "cloud-streamed placeholders: never a space target", "never"),
    ];

    // Names unambiguous anywhere on the volume.
    private static readonly Dictionary<string, string> Anywhere = new(StringComparer.Ordinal)
    {
        ["node_modules"] = "node_modules",
        [".venv"] = "python venv",
        ["venv"] = "python venv",
        ["__pycache__"] = "tool caches",
        [".pytest_cache"] = "tool caches",
        [".ruff_cache"] = "tool caches",
        [".mypy_cache"] = "tool caches",
        [".git"] = ".git object stores",
    };

    // Names that only mean "build output" inside the code root (Program Files has a `bin` too).
    private static readonly Dictionary<string, string> CodeOnly = new(StringComparer.Ordinal)
    {
        ["temp"] = "scratch in temp/",
        [".worktrees"] = "worktrees",
        ["packages"] = "NuGet packages",
        ["bin"] = "build output",
        ["obj"] = "build output",
        ["dist"] = "build output",
        ["build"] = "build output",
        ["target"] = "build output",
        [".next"] = "build output",
        [".turbo"] = "build output",
    };

    private static readonly Regex PercentVariable = new("%([^%]+)%", RegexOptions.CultureInvariant);

    /// <summary>
    /// The folders in a WizTree export at or above <paramref name="floor"/> allocated bytes, in file order. Row 1 is a
    /// banner, folder names hold commas, and only the root row fills the four drive-level columns, so the rows are
    /// ragged: columns are read by position, a short row's missing ones as empty.
    /// </summary>
    public static IReadOnlyList<Folder> Load(string csv, long floor = 0)
    {
        var folders = new List<Folder>();
        using var reader = new StreamReader(csv, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        _ = reader.ReadLine();
        _ = reader.ReadLine();
        var number = 2;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (line.Length == 0)
            {
                continue;
            }

            var cells = Cells(line);
            var allocated = Integer(cells, 2, number);
            if (allocated is null || allocated < floor)
            {
                continue;
            }

            folders.Add(new Folder(cells[0].TrimEnd('\\'), allocated.Value, Integer(cells, 1, number) ?? 0, Integer(cells, 5, number) ?? 0));
        }

        return folders;
    }

    /// <summary>Depth-first drill-down from each drive root: a folder's children at or above <paramref name="minBytes"/>, largest first.</summary>
    public static IReadOnlyList<TreeRow> Tree(IEnumerable<Folder> folders, long minBytes, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var children = new Dictionary<string, List<Folder>>(StringComparer.Ordinal);
        var roots = new List<Folder>();
        foreach (var folder in folders.Where(folder => folder.Allocated >= minBytes))
        {
            if (Depth(folder.Path) == 0)
            {
                roots.Add(folder);
            }
            else
            {
                var parent = Parent(folder.Path).ToLowerInvariant();
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }

                list.Add(folder);
            }
        }

        var rows = new List<TreeRow>();
        void Walk(Folder node, int level)
        {
            rows.Add(new TreeRow(level, node));
            if (level >= maxDepth)
            {
                return;
            }

            foreach (var child in (children.GetValueOrDefault(node.Path.ToLowerInvariant()) ?? []).OrderByDescending(child => child.Allocated))
            {
                Walk(child, level + 1);
            }
        }

        foreach (var root in roots.OrderByDescending(root => root.Allocated))
        {
            Walk(root, 0);
        }

        return rows;
    }

    /// <summary>
    /// Allocated bytes per kind of dev artefact, largest first, each folder counted once at its outermost match:
    /// <c>temp\</c> holds <c>packages\</c> which holds <c>bin\</c>, and summing them would triple-count. Sorted by path a
    /// folder's descendants follow it, so one pass with a consumed prefix attributes each subtree to its outermost match.
    /// The rest of <paramref name="codeRoot"/> is the source; more than the root holds means hardlinks counted twice.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, long>> Partition(IReadOnlyCollection<Folder> folders, string codeRoot)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(codeRoot);
        var root = codeRoot.TrimEnd('\\').ToLowerInvariant() + "\\";
        var totals = new List<KeyValuePair<string, long>>();
        string? consumed = null;

        // Sorted with the trailing separator: bare, `repo-b` sorts between `repo` and `repo\x`.
        foreach (var (folder, key) in folders.Select(folder => (folder, Key: folder.Path.ToLowerInvariant() + "\\")).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (consumed is not null && key.StartsWith(consumed, StringComparison.Ordinal))
            {
                continue;
            }

            consumed = null;
            if (Category(folder.Path, root) is not { } category)
            {
                continue;
            }

            Add(totals, category, folder.Allocated);
            consumed = key;
        }

        var codeTotal = folders.FirstOrDefault(folder => string.Equals(folder.Path.ToLowerInvariant(), root.TrimEnd('\\'), StringComparison.Ordinal))?.Allocated ?? 0;
        if (codeTotal != 0)
        {
            var inside = totals.Sum(total => total.Value);

            // WizTree charges a hardlinked file to every folder linking it; the root counts it once.
            Add(totals, inside <= codeTotal ? "source + everything else (code root)" : "hardlinks shared across categories (subtract)", codeTotal - inside);
        }

        return [.. totals.OrderByDescending(total => total.Value)];
    }

    /// <summary>Each hog in <paramref name="catalogue"/> found on the volume, with its allocated total, largest first.</summary>
    /// <param name="folders">The export.</param>
    /// <param name="catalogue">The hogs to look for; <see cref="Catalogue"/> outside tests.</param>
    /// <param name="environment">Reads the <c>%VAR%</c> a pattern names; one it does not know stays as written.</param>
    public static IReadOnlyList<HogHit> Hogs(IReadOnlyCollection<Folder> folders, IEnumerable<Hog> catalogue, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(catalogue);
        var found = new List<HogHit>();
        foreach (var hog in catalogue)
        {
            var pattern = PercentVariable.Replace(hog.Pattern, match => environment(match.Groups[1].Value) ?? match.Value).TrimEnd('\\');
            var segments = pattern.ToLowerInvariant().Split('\\');
            var hits = folders.Where(folder => Depth(folder.Path) == segments.Length - 1 && Matches(folder.Path, segments)).ToList();
            if (hits.Count > 0)
            {
                found.Add(new HogHit(hog, hits.Sum(hit => hit.Allocated), [.. hits.Select(hit => hit.Path)]));
            }
        }

        return [.. found.OrderByDescending(hit => hit.Allocated)];
    }

    /// <summary>Bytes in files at the volume root (pagefile.sys, hiberfil.sys, swapfile.sys): the root less its top-level folders.</summary>
    public static long RootFiles(IReadOnlyCollection<Folder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (folders.FirstOrDefault(folder => Depth(folder.Path) == 0) is not { } root)
        {
            return 0;
        }

        return Math.Max(root.Allocated - folders.Where(folder => Depth(folder.Path) == 1).Sum(folder => folder.Allocated), 0);
    }

    private static int Depth(string path) => path.Count(c => c == '\\');

    private static string Parent(string path) => path.LastIndexOf('\\') is var at and >= 0 ? path[..at] : string.Empty;

    private static string? Category(string path, string codeRoot)
    {
        var name = path[(path.LastIndexOf('\\') + 1)..].ToLowerInvariant();
        return Anywhere.TryGetValue(name, out var anywhere) ? anywhere
            : path.ToLowerInvariant().StartsWith(codeRoot, StringComparison.Ordinal) && CodeOnly.TryGetValue(name, out var code) ? code
            : null;
    }

    private static void Add(List<KeyValuePair<string, long>> totals, string category, long bytes)
    {
        var at = totals.FindIndex(total => total.Key == category);
        if (at < 0)
        {
            totals.Add(new(category, bytes));
        }
        else
        {
            totals[at] = new(category, totals[at].Value + bytes);
        }
    }

    // Each segment as fnmatch matches it: `*` any run and `?` any one character, within the segment.
    private static bool Matches(string path, string[] segments)
    {
        var parts = path.ToLowerInvariant().Split('\\');
        return parts.Length == segments.Length && parts.Zip(segments).All(pair => Glob(pair.Second).IsMatch(pair.First));
    }

    private static Regex Glob(string segment) =>
        new("^" + Regex.Escape(segment).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // A CSV line split at commas outside quotes, with "" inside quotes standing for one ".
    private static List<string> Cells(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"')
                {
                    _ = cell.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    _ = cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                _ = cell.Clear();
            }
            else
            {
                _ = cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }

    private static long? Integer(List<string> cells, int column, int line)
    {
        if (column >= cells.Count || cells[column].Length == 0)
        {
            return null;
        }

        return long.TryParse(cells[column], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"line {line}: column {column + 1} is not a whole number: {cells[column]}");
    }
}

/// <summary>
/// Runs WizTree's folder export of a drive and waits for it. <c>/admin=1</c> relaunches elevated, so the launcher returns
/// within seconds while a child keeps writing: done is "the child has exited", not "the launcher exited" and not "the
/// file stopped growing".
/// </summary>
/// <param name="proc">Runs WizTree and <c>tasklist</c>.</param>
/// <param name="time">The clock the deadline is measured on.</param>
/// <param name="sleep">Waits between checks.</param>
/// <param name="wizTree">WizTree's executable; <see cref="Volume.WizTree"/> outside tests.</param>
public sealed class WizTreeExport(IProc proc, TimeProvider time, Func<TimeSpan, CancellationToken, Task> sleep, string wizTree = Volume.WizTree)
{
    /// <summary>How long the export may take, UAC prompt included.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(900);

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

    /// <summary>Exports <paramref name="drive"/>'s folders to <paramref name="output"/>, replacing any earlier export.</summary>
    /// <exception cref="FileNotFoundException">WizTree is not installed.</exception>
    /// <exception cref="TimeoutException">The export did not finish in time.</exception>
    public async Task RunAsync(string drive, string output, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drive);
        if (!File.Exists(wizTree))
        {
            throw new FileNotFoundException($"WizTree not found at {wizTree}: winget install --id AntibodySoftware.WizTree");
        }

        File.Delete(output);
        var root = drive.TrimEnd(':', '\\', '/') + ":\\";
        _ = await proc.RunAsync(new ProcRequest([wizTree, root, $"/export={output}", "/admin=1", "/exportfolders=1", "/exportfiles=0"]), cancellationToken).ConfigureAwait(false);
        var limit = timeout ?? DefaultTimeout;
        var started = time.GetTimestamp();
        while (time.GetElapsedTime(started) < limit)
        {
            await sleep(Poll, cancellationToken).ConfigureAwait(false);

            // The export pauses mid-write while buffering; only the child exiting means done.
            if (File.Exists(output) && new FileInfo(output).Length > 0 && !await RunningAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new TimeoutException($"WizTree export did not complete within {limit.TotalSeconds:0}s (was the UAC prompt approved?)");
    }

    private async Task<bool> RunningAsync(CancellationToken cancellationToken)
    {
        var image = Path.GetFileName(wizTree.Replace('\\', '/'));
        var ran = await proc.RunAsync(new ProcRequest(["tasklist", "/FI", $"IMAGENAME eq {image}", "/NH"]), cancellationToken).ConfigureAwait(false);
        return ran.Out.Contains(image, StringComparison.OrdinalIgnoreCase);
    }
}
