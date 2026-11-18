using Rig.Core.Processes;

namespace Rig.Core.Vault;

/// <summary>What a Drive backup would hold, measured against the remote's <c>main</c>.</summary>
/// <param name="Base">The commit of <c>origin/main</c> the Drive copy was compared against.</param>
/// <param name="Differ">Notes, relative to the vault, whose Drive copy differs from <c>main</c>'s.</param>
public sealed record DriveBackupPlan(string Base, IReadOnlyList<string> Differ);

/// <summary>A pushed backup.</summary>
/// <param name="Branch">The branch it was pushed to.</param>
/// <param name="Commit">Its commit.</param>
public sealed record DriveBackupResult(string Branch, string Commit);

/// <summary>A git step failed; the message names it and what git said.</summary>
public sealed class DriveBackupException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DriveBackupException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public DriveBackupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and its cause.</summary>
    public DriveBackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Before a reconcile overwrites Drive, commits the Drive copy of every note that differs from <c>origin/main</c> onto
/// <c>main</c> and pushes it as a branch, so a conflict resolved in git's favour stays recoverable. Built with git
/// plumbing against a private index: no worktree, no checkout, no LFS smudge, and the vault checkout is never touched,
/// whatever branch it is on. Only notes git has are compared; a Drive-only note is never overwritten, so it needs no
/// backup.
/// </summary>
/// <param name="git">Runs git in the vault's repository.</param>
/// <param name="vault">The git copy of the vault: a folder at the top of its repository.</param>
/// <param name="drive">The Drive copy of the vault.</param>
public sealed class DriveBackup(Git git, string vault, string drive)
{
    /// <summary>Folders a reconcile never compares, as <c>vault-reconcile</c> skips them.</summary>
    public static readonly IReadOnlySet<string> SkippedFolders = new HashSet<string>(StringComparer.Ordinal) { ".obsidian", ".git", ".trash", "books" };

    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(2);

    private string Repo => Path.GetDirectoryName(Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;

    private string Prefix => Path.GetFileName(Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>Fetches <c>main</c> and finds the notes whose Drive copy differs from it.</summary>
    public async Task<DriveBackupPlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        _ = await RunAsync("fetch main", ["fetch", "-q", "origin", "main"], NetworkTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        var head = (await RunAsync("read origin/main", ["rev-parse", "--verify", "origin/main^{commit}"], cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();

        // NUL-delimited, so a name with spaces or quotes is never C-quoted out of matching.
        var tree = await RunAsync("list main's notes", ["ls-tree", "-r", "-z", head, "--", Prefix], cancellationToken: cancellationToken).ConfigureAwait(false);
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> blob <sha>\t<path>"
            var tab = entry.IndexOf('\t', StringComparison.Ordinal);
            var meta = entry[..tab].Split(' ');
            var path = entry[(tab + 1)..];
            if (meta[1] == "blob" && path.EndsWith(".md", StringComparison.Ordinal))
            {
                blobs[path[(Prefix.Length + 1)..]] = meta[2];
            }
        }

        var shared = DriveNotes().Where(blobs.ContainsKey).ToList();
        var hashes = await HashAsync(shared, write: false, cancellationToken).ConfigureAwait(false);
        return new DriveBackupPlan(head, [.. shared.Where((note, i) => hashes[i] != blobs[note])]);
    }

    /// <summary>Commits the Drive copies <paramref name="plan"/> found onto its base and pushes them as <paramref name="branch"/>.</summary>
    public async Task<DriveBackupResult> PushAsync(DriveBackupPlan plan, string branch, string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var index = Path.Join(Path.GetTempPath(), $"rig-drive-backup-{Guid.NewGuid():N}.index");
        var privateIndex = new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = index };
        try
        {
            var blobs = await HashAsync(plan.Differ, write: true, cancellationToken).ConfigureAwait(false);
            _ = await RunAsync("load main into a private index", ["read-tree", plan.Base], environment: privateIndex, cancellationToken: cancellationToken).ConfigureAwait(false);
            var entries = string.Concat(plan.Differ.Select((note, i) => $"100644 {blobs[i]}\t{Prefix}/{note}\0"));
            _ = await RunAsync("stage the Drive copies", ["update-index", "-z", "--index-info"], environment: privateIndex, stdin: entries, cancellationToken: cancellationToken).ConfigureAwait(false);
            var tree = (await RunAsync("write the tree", ["write-tree"], environment: privateIndex, cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();
            var commit = (await RunAsync("commit", ["commit-tree", tree, "-p", plan.Base, "-m", message], cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();
            _ = await RunAsync("push the backup", ["push", "-q", "origin", $"{commit}:refs/heads/{branch}"], NetworkTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new DriveBackupResult(branch, commit);
        }
        finally
        {
            File.Delete(index);
        }
    }

    /// <summary>Every readable note in the Drive copy, relative to it with <c>/</c> separators, skipping the folders a reconcile skips.</summary>
    public IEnumerable<string> DriveNotes()
    {
        var root = Path.GetFullPath(drive);
        return Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(note => !note.Split('/').Any(SkippedFolders.Contains) && !note.Contains('\n', StringComparison.Ordinal))
            .Where(note => File.Exists(Path.Join(root, note)))
            .Order(StringComparer.Ordinal);
    }

    // Raw bytes, no filters: the vault's notes have no clean filter, so this is the id git stores for the same content.
    private async Task<IReadOnlyList<string>> HashAsync(IReadOnlyList<string> notes, bool write, CancellationToken cancellationToken)
    {
        if (notes.Count == 0)
        {
            return [];
        }

        string[] args = write ? ["hash-object", "-w", "--no-filters", "--stdin-paths"] : ["hash-object", "--no-filters", "--stdin-paths"];
        var paths = string.Concat(notes.Select(note => Path.Join(Path.GetFullPath(drive), note) + "\n"));
        var hashes = (await RunAsync("hash the Drive copies", args, stdin: paths, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return hashes.Length == notes.Count ? hashes : throw new DriveBackupException($"hash the Drive copies: git returned {hashes.Length} ids for {notes.Count} notes");
    }

    private async Task<string> RunAsync(
        string step,
        IReadOnlyList<string> args,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? stdin = null,
        CancellationToken cancellationToken = default)
    {
        var ran = await git.RunAsync(Repo, args, timeout, environment, stdin, cancellationToken).ConfigureAwait(false);
        return ran.Ok ? ran.Out : throw new DriveBackupException($"{step} failed: {ran.Err.Trim()}");
    }
}
