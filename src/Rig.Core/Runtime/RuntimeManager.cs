using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Text;

namespace Rig.Core.Runtime;

/// <summary>The copies runtime uses: the commit in use, the one kept for rollback, and the checkout they came from.</summary>
/// <param name="Current">The short sha of the copy in use.</param>
/// <param name="Previous">The short sha of the copy kept for rollback.</param>
/// <param name="Repo">The checkout the copies were built from.</param>
public sealed record RuntimeState(string? Current = null, string? Previous = null, string? Repo = null);

/// <summary>What a copy's <c>installed.json</c> records.</summary>
/// <param name="Sha">The full commit sha the copy was extracted from.</param>
/// <param name="Ref">The ref it was installed as.</param>
/// <param name="Repo">The checkout it came from.</param>
/// <param name="InstalledAt">When it was installed, in ISO 8601.</param>
public sealed record InstalledInfo(string? Sha = null, string? Ref = null, string? Repo = null, string? InstalledAt = null);

/// <summary>A step of an install or rollback failed; the message says which and why.</summary>
public sealed class RuntimeInstallException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RuntimeInstallException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    /// <param name="message">What failed and why.</param>
    public RuntimeInstallException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and its cause.</summary>
    /// <param name="message">What failed and why.</param>
    /// <param name="innerException">The cause.</param>
    public RuntimeInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>How an install ended.</summary>
/// <param name="Sha">The full commit sha.</param>
/// <param name="Copy">The copy's folder.</param>
/// <param name="Changed">Whether <c>current</c> moved.</param>
/// <param name="Previous">The copy kept for rollback.</param>
/// <param name="Removed">The old copies removed.</param>
public sealed record RuntimeInstallResult(string Sha, string Copy, bool Changed, string? Previous, IReadOnlyList<string> Removed);

/// <summary>What <c>rig runtime status</c> shows.</summary>
/// <param name="Root">The runtime folder.</param>
/// <param name="Link">The <c>current</c> link.</param>
/// <param name="State">The recorded current and previous copies.</param>
/// <param name="Current">The current copy's metadata, or null when nothing is installed.</param>
/// <param name="Previous">The previous copy's metadata, or null when none is kept.</param>
/// <param name="Behind">How many commits the current copy is behind the compared ref, or null when unknown.</param>
public sealed record RuntimeStatus(string Root, string Link, RuntimeState State, InstalledInfo? Current, InstalledInfo? Previous, int? Behind);

/// <summary>
/// The pinned copy of one commit that the scheduled tasks run from. Ported from <c>desktop_manager.runtime</c>:
/// each copy is a plain folder under <c>&lt;app data&gt;/runtime/&lt;sha12&gt;</c>, with its own environment and
/// published <c>rig</c>, and <c>current</c> is the link the tasks and the PATH shim follow.
/// </summary>
/// <param name="proc">Runs git, uv, dotnet and, on Windows, <c>mklink</c>.</param>
/// <param name="root">The runtime folder, normally <c>&lt;app data&gt;/runtime</c>.</param>
/// <param name="clock">The clock <c>installed.json</c> stamps its time with.</param>
public sealed class RuntimeManager(IProc proc, string root, TimeProvider clock)
{
    /// <summary>The length of a copy's folder name.</summary>
    public const int ShortShaLength = 12;

    /// <summary>The link the tasks and the shim follow.</summary>
    public const string LinkName = "current";

    /// <summary>The file recording current and previous.</summary>
    public const string StateName = "state.json";

    /// <summary>The file recording what a copy was built from.</summary>
    public const string InstalledName = "installed.json";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(30);
    private static readonly IReadOnlyList<string> Unset = ["VIRTUAL_ENV", "UV_PROJECT_ENVIRONMENT"];
    private static readonly IReadOnlyDictionary<string, string> NoPrompt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    /// <summary>The runtime folder under <paramref name="appData"/>.</summary>
    public static string RuntimeRoot(string appData) => Path.Join(appData, "runtime");

    /// <summary>The <c>current</c> link under <paramref name="root"/>.</summary>
    public static string CurrentLink(string root) => Path.Join(root, LinkName);

    /// <summary>Whether <paramref name="path"/> belongs to an installed copy rather than a checkout.</summary>
    public static bool RunningCopy(string path, string root) =>
        PathResolution.IsUnder(PathResolution.Resolve(path), PathResolution.Resolve(root));

    /// <summary>A copy's environment executable, such as its own <c>python</c> or <c>desktop-manager</c>.</summary>
    public static string Executable(string copy, string name, bool windows) =>
        Path.Join(copy, ".venv", windows ? "Scripts" : "bin", windows ? $"{name}.exe" : name);

    /// <summary>A copy's published <c>rig</c> or <c>rigw</c>, as <c>dotnet publish</c> wrote it.</summary>
    public static string Published(string copy, string name, bool windows) =>
        Path.Join(copy, "rig", windows ? $"{name}.exe" : name);

    /// <summary>The folder this manager keeps its copies in.</summary>
    public string Root => root;

    /// <summary>The recorded current and previous copies; empty when nothing is installed.</summary>
    public RuntimeState ReadState()
    {
        var path = Path.Join(root, StateName);
        if (!File.Exists(path))
        {
            return new RuntimeState();
        }

        var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))
            ?? throw new InvalidDataException($"{path} is empty.");
        return node is JsonObject state
            ? new RuntimeState(Text(state, "current"), Text(state, "previous"), Text(state, "repo"))
            : throw new InvalidDataException($"{path} is not a JSON object.");
    }

    /// <summary>Writes <paramref name="state"/> as Python's <c>json.dumps(indent=2)</c> did, replacing it atomically.</summary>
    /// <param name="state">The state to record.</param>
    public void WriteState(RuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _ = Directory.CreateDirectory(root);
        WriteJson(
            Path.Join(root, StateName),
            new JsonObject
            {
                ["current"] = state.Current,
                ["previous"] = state.Previous,
                ["repo"] = state.Repo,
            });
    }

    /// <summary>A copy's <c>installed.json</c>, or all-null when the copy has none.</summary>
    /// <param name="copy">The copy's folder.</param>
    public static InstalledInfo ReadInstalled(string copy)
    {
        var path = Path.Join(copy, InstalledName);
        if (!File.Exists(path))
        {
            return new InstalledInfo();
        }

        var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))
            ?? throw new InvalidDataException($"{path} is empty.");
        return node is JsonObject installed
            ? new InstalledInfo(Text(installed, "sha"), Text(installed, "ref"), Text(installed, "repo"), Text(installed, "installed_at"))
            : throw new InvalidDataException($"{path} is not a JSON object.");
    }

    /// <summary>The copy in use, the one kept for rollback, and how far the current one is behind <paramref name="refName"/>.</summary>
    /// <param name="refName">The ref to compare with, as of the repo's last fetch.</param>
    public RuntimeStatus Status(string refName)
    {
        var state = ReadState();
        var current = state.Current is { Length: > 0 } name ? ReadInstalled(Path.Join(root, name)) : null;
        var previous = state.Previous is { Length: > 0 } previousName ? ReadInstalled(Path.Join(root, previousName)) : null;
        var behind = state.Repo is { Length: > 0 } repo && current?.Sha is { Length: > 0 } sha
            ? CommitsBehind(repo, sha, refName)
            : null;
        return new RuntimeStatus(root, CurrentLink(root), state, current, previous, behind);
    }

    /// <summary>
    /// Builds a copy of <paramref name="refName"/> from <paramref name="repo"/>, checks it runs, then makes it current.
    /// The copy it replaces is kept for rollback. Installing the commit already current changes nothing.
    /// </summary>
    /// <param name="repo">The checkout to take the commit from.</param>
    /// <param name="refName">The commit to install.</param>
    /// <param name="fetch">Whether to fetch origin first for an <c>origin/*</c> ref.</param>
    /// <param name="progress">Reports each step, as the Python printed it.</param>
    public RuntimeInstallResult Install(string repo, string refName, bool fetch = true, Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(refName);
        _ = Directory.CreateDirectory(root);
        if (!Directory.Exists(repo))
        {
            throw new RuntimeInstallException($"{repo} is not a checkout: no such folder.");
        }

        repo = PathResolution.Resolve(repo);
        var state = ReadState();
        var sha = ResolveCommit(repo, refName, fetch);
        var name = sha[..Math.Min(sha.Length, ShortShaLength)];
        var copy = Path.Join(root, name);
        if (state.Current == name && Path.Exists(CurrentLink(root)))
        {
            return new RuntimeInstallResult(sha, copy, Changed: false, state.Previous, []);
        }

        if (!File.Exists(Path.Join(copy, InstalledName)))
        {
            var staging = Path.Join(root, name + ".partial");
            TryRemoveTree(staging);
            TryRemoveTree(copy);
            progress?.Invoke($"Extracting {name} from {repo}");
            Extract(repo, sha, staging);
            Directory.Move(staging, copy);
            progress?.Invoke("Building its environment from uv.lock and publishing rig");
            try
            {
                BuildEnvironment(copy);
                Publish(copy);
            }
            catch (RuntimeInstallException)
            {
                TryRemoveTree(copy);
                throw;
            }
        }

        progress?.Invoke("Checking it runs");
        var problems = Check(copy);
        if (problems.Count > 0)
        {
            if (!File.Exists(Path.Join(copy, InstalledName)))
            {
                TryRemoveTree(copy);
            }

            throw new RuntimeInstallException(
                "the new copy failed its checks; current is unchanged:\n- " + string.Join("\n- ", problems));
        }

        WriteInstalled(
            copy,
            new InstalledInfo(sha, refName, repo, clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture)));
        PointCurrent(copy);
        var previous = state.Current != name ? state.Current : state.Previous;
        try
        {
            WriteState(new RuntimeState(name, previous, repo));
        }
        catch
        {
            TryRestoreCurrent(state.Current);
            throw;
        }

        var removed = Prune(previous is null ? [name] : [name, previous]);
        return new RuntimeInstallResult(sha, copy, Changed: true, previous, removed);
    }

    /// <summary>Points <c>current</c> at the copy kept for rollback and makes the copy it replaces the new previous.</summary>
    /// <exception cref="RuntimeInstallException">There is no previous copy to roll back to.</exception>
    public RuntimeState Rollback()
    {
        var state = ReadState();
        if (state.Previous is not { Length: > 0 } previous || !File.Exists(Path.Join(root, previous, InstalledName)))
        {
            throw new RuntimeInstallException("no previous copy to roll back to");
        }

        PointCurrent(Path.Join(root, previous));
        var swapped = new RuntimeState(previous, state.Current, state.Repo);
        try
        {
            WriteState(swapped);
        }
        catch
        {
            TryRestoreCurrent(state.Current);
            throw;
        }

        return swapped;
    }

    /// <summary>
    /// Repoints <c>current</c> at <paramref name="copy"/>: a directory junction on Windows, a symlink elsewhere, and a
    /// directory copy where neither can be made. A link that cannot be replaced puts the old one back.
    /// </summary>
    /// <param name="copy">The copy to make current.</param>
    public void PointCurrent(string copy)
    {
        var link = CurrentLink(root);
        string? old = null;
        string? backup = null;
        if (IsLink(link))
        {
            old = LinkTarget(link);
            RemoveLink(link);
        }
        else if (Path.Exists(link) || Directory.Exists(link) || File.Exists(link))
        {
            if (!Directory.Exists(link) || !File.Exists(Path.Join(link, InstalledName)))
            {
                throw new RuntimeInstallException($"{link} exists and is not a link; move it aside");
            }

            backup = link + ".backup." + Guid.NewGuid().ToString("N");
            Directory.Move(link, backup);
        }

        try
        {
            try
            {
                MakeLink(link, copy);
            }
            catch (RuntimeInstallException ex) when (HandleWindowsCopyFallback(ex, link, copy, old))
            {
            }

            if (backup is not null && Directory.Exists(backup))
            {
                TryRemoveTree(backup);
            }
        }
        catch
        {
            RestoreBackupOrOldLink(link, old, backup);
            throw;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Link replacement failure recovery")]
    private void RestoreBackupOrOldLink(string link, string? old, string? backup)
    {
        if (backup is not null && !Path.Exists(link) && Directory.Exists(backup))
        {
            try { Directory.Move(backup, link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        else if (old is not null && !Path.Exists(link))
        {
            try { MakeLink(link, old); } catch (RuntimeInstallException) { } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Install rollback failure recovery")]
    private void TryRestoreCurrent(string? current)
    {
        if (current is not null && Directory.Exists(Path.Join(root, current)))
        {
            try { PointCurrent(Path.Join(root, current)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>How many commits <paramref name="refName"/> is ahead of <paramref name="sha"/>, or null when git cannot say.</summary>
    /// <param name="repo">The checkout to ask.</param>
    /// <param name="sha">The installed commit.</param>
    /// <param name="refName">The ref to compare with.</param>
    public int? CommitsBehind(string repo, string sha, string refName)
    {
        var ran = Run(["git", "-C", repo, "rev-list", "--count", $"{sha}..{refName}"], null);
        var output = (ran.Out + ran.Err).Trim();
        return ran.Ok && int.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
    }

    private string ResolveCommit(string repo, string refName, bool fetch)
    {
        if (fetch && refName.StartsWith("origin/", StringComparison.Ordinal))
        {
            _ = Must(["git", "-C", repo, "fetch", "--quiet", "origin"], null);
        }

        var resolved = Must(["git", "-C", repo, "rev-parse", "--verify", $"{refName}^{{commit}}"], null).Trim();
        if (resolved.Length == 0)
        {
            throw new RuntimeInstallException($"Could not resolve {refName} in {repo}.");
        }

        return resolved;
    }

    private void Extract(string repo, string sha, string destination)
    {
        var archive = Path.Join(root, $".{sha}.{Guid.NewGuid():N}.tar");
        try
        {
            _ = Must(["git", "-C", repo, "archive", "--format=tar", $"--output={archive}", sha], null);
            _ = Directory.CreateDirectory(destination);
            TarFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    private void BuildEnvironment(string copy) =>
        _ = Must(["uv", "sync", "--project", copy, "--frozen", "--no-dev", "--link-mode", "copy"], copy);

    private void Publish(string copy) =>
        _ = Must(["dotnet", "publish", Path.Join(copy, "src", "Rig.Launcher"), "-c", "Release", "-o", Path.Join(copy, "rig"), "-m:2", "-v", "q"], copy);

    private List<string> Check(string copy)
    {
        var windows = OperatingSystem.IsWindows();
        var desktopManager = Executable(copy, "desktop-manager", windows);
        var python = Executable(copy, "python", windows);
        List<IReadOnlyList<string>> commands =
        [
            [desktopManager, "--help"],
            [Published(copy, "rig", windows), "chrome-tabs", "organise", "--help"],
            [Published(copy, "rig", windows), "jobs", "run", "--help"],
            [Published(copy, "rig", windows), "jobs", "install", "--help"],
            windows
                ? [python, "-c", "import desktop_manager.windows_workspace"]
                : [python, "-c", "import rig.web_host.server"],
        ];
        var plan = Path.Join(copy, "desktop-manager", "workspace-plan.bundle.json");
        if (File.Exists(plan))
        {
            commands.Add([Published(copy, "rig", windows), "desktop", "workspace", "validate-plan", "--config", plan]);
        }

        var problems = new List<string>();
        foreach (var command in commands)
        {
            var ran = Run(command, copy);
            if (ran.Ok)
            {
                continue;
            }

            var output = (ran.Out + ran.Err).Trim();
            problems.Add($"{string.Join(' ', command.Skip(1))} exited {ran.Code}: {Tail(output, 2000)}");
        }

        return problems;
    }

    private static string Tail(string text, int length) => text.Length <= length ? text : text[^length..];

    private static void WriteInstalled(string copy, InstalledInfo installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        WriteJson(
            Path.Join(copy, InstalledName),
            new JsonObject
            {
                ["sha"] = installed.Sha,
                ["ref"] = installed.Ref,
                ["repo"] = installed.Repo,
                ["installed_at"] = installed.InstalledAt,
            });
    }

    private List<string> Prune(IEnumerable<string> keep)
    {
        var kept = keep.ToHashSet(StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            if (kept.Contains(name) || name == LinkName || IsLink(entry) || !Directory.Exists(entry))
            {
                continue;
            }

            if (!File.Exists(Path.Join(entry, InstalledName)) && !name.EndsWith(".partial", StringComparison.Ordinal))
            {
                continue;
            }

            TryRemoveTree(entry);
            if (!Path.Exists(entry))
            {
                removed.Add(name);
            }
        }

        return removed;
    }

    [ExcludeFromCodeCoverage(Justification = "Windows directory copy fallback; tested in Windows CI")]
    internal static bool HandleWindowsCopyFallback(RuntimeInstallException ex, string link, string copy, string? old)
    {
        _ = ex;
        if (OperatingSystem.IsWindows() && old is null && Directory.Exists(copy))
        {
            FallbackWindowsDirectoryCopy(link, copy);
            return true;
        }

        return false;
    }

    [ExcludeFromCodeCoverage(Justification = "Platform-specific directory link creation")]
    private void MakeLink(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return;
            }

            MakeWindowsJunction(link, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeInstallException($"could not link {link} to {target}: {error.Message}", error);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows junction fallback")]
    private void MakeWindowsJunction(string link, string target)
    {
        var ran = Run(["cmd", "/c", "mklink", "/J", link, target], null);
        if (!ran.Ok)
        {
            throw new RuntimeInstallException($"mklink /J {link} failed: {(ran.Err + ran.Out).Trim()}");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows junction fallback")]
    private static void FallbackWindowsDirectoryCopy(string link, string copy)
    {
        var tempDir = link + ".tmpdir." + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(copy, tempDir);
            if (Directory.Exists(link)) { RemoveTree(link); }
            Directory.Move(tempDir, link);
        }
        catch
        {
            if (Directory.Exists(tempDir)) { TryRemoveTree(tempDir); }
            throw;
        }
    }

    internal static string LinkTarget(string link)
    {
        var target = new DirectoryInfo(link).LinkTarget;
        if (string.IsNullOrEmpty(target))
        {
            throw new RuntimeInstallException($"{link} is not a link");
        }

        return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Join(Path.GetDirectoryName(link), target));
    }

    internal static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Defensive link removal fallback")]
    internal static void RemoveLink(string link)
    {
        try
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            else
            {
                File.Delete(link);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(link); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { Directory.Delete(link); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows junction fallback")]
    internal static void CopyDirectory(string source, string destination)
    {
        _ = Directory.CreateDirectory(destination);
        foreach (var folder in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            _ = Directory.CreateDirectory(Path.Join(destination, Path.GetRelativePath(source, folder)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Join(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Defensive tree removal fallback")]
    internal static void TryRemoveTree(string path)
    {
        try
        {
            RemoveTree(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The copy stays for a later install, as Python's rmtree(ignore_errors=True) left it.
        }
    }

    internal static void RemoveTree(string path)
    {
        if (!Path.Exists(path) && !IsLink(path))
        {
            return;
        }

        if (IsLink(path))
        {
            RemoveLink(path);
            return;
        }

        if (Directory.Exists(path))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                RemoveTree(entry);
            }

            Directory.Delete(path);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void WriteJson(string path, JsonObject json)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString(JsonOutputOptions.IndentedUnicode) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string? Text(JsonObject parent, string name) =>
        JsonShape.Field(parent, name) is { } node
            ? JsonShape.AsText(node) ?? throw new InvalidDataException($"Field '{name}' must be a string or null.")
            : null;

    private string Must(IReadOnlyList<string> command, string? workingDirectory)
    {
        var ran = Run(command, workingDirectory);
        var output = (ran.Out + ran.Err).Trim();
        return ran.Ok ? output : throw new RuntimeInstallException($"{string.Join(' ', command)} failed ({ran.Code}): {output}");
    }

    private Ran Run(IReadOnlyList<string> command, string? workingDirectory) =>
        proc.Run(new ProcRequest(command)
        {
            WorkingDirectory = workingDirectory,
            Timeout = command[0] is "uv" or "dotnet" ? SyncTimeout : CommandTimeout,
            Unset = Unset,
            Environment = NoPrompt,
        });
}
