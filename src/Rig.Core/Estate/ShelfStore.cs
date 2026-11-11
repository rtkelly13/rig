using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>Whether a shelf step worked, and its output: stdout, stripped, on success, the error otherwise.</summary>
/// <param name="Ok">The step worked.</param>
/// <param name="Detail">What it printed, or why it failed.</param>
public sealed record ShelfStep(bool Ok, string Detail);

/// <summary>origin could not be asked which shelves it holds. Never read as "none": a push or prune would act blind.</summary>
public sealed class ShelfStoreException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ShelfStoreException"/> class.</summary>
    public ShelfStoreException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ShelfStoreException"/> class.</summary>
    /// <param name="message">Why origin could not be read.</param>
    public ShelfStoreException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ShelfStoreException"/> class.</summary>
    /// <param name="message">Why origin could not be read.</param>
    /// <param name="innerException">The cause.</param>
    public ShelfStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The <c>Shelved-*</c> trailers of a marker commit; empty where one is absent.</summary>
/// <param name="Branch">The branch the worktree had checked out.</param>
/// <param name="Base">The commit the marker sits on.</param>
/// <param name="Path">Where the worktree was, relative to its clone when inside it.</param>
/// <param name="Pr">The PR the branch headed, as <c>#n</c>.</param>
/// <param name="At">When it was shelved.</param>
/// <param name="By">What shelved it.</param>
public sealed record ShelfTrailers(string Branch = "", string Base = "", string Path = "", string Pr = "", string At = "", string By = "");

/// <summary>One shelf on origin: a ref under <c>refs/heads/shelved/</c> whose tip is a <c>TEMP-SHELVED:</c> commit.</summary>
/// <param name="Repo">The clone it was read from.</param>
/// <param name="Ref">The full ref.</param>
/// <param name="Sha">The marker commit.</param>
/// <param name="Subject">The marker's subject line.</param>
/// <param name="Trailers">The marker's trailers.</param>
/// <param name="Snapshot">The marker carries file changes; false for a clean worktree's local commits.</param>
public sealed record Shelf(string Repo, string Ref, string Sha, string Subject, ShelfTrailers Trailers, bool Snapshot)
{
    /// <summary>The shelf name: the ref with <c>refs/heads/shelved/&lt;user&gt;/</c> stripped.</summary>
    public string Name
    {
        get
        {
            var rest = Ref.StartsWith(ShelfStore.RefRoot + "/", StringComparison.Ordinal) ? Ref[(ShelfStore.RefRoot.Length + 1)..] : Ref;
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? rest : rest[(slash + 1)..];
        }
    }

    /// <summary>The shelf as <c>estate shelved --json</c> prints it: the trailers that are set follow the fixed fields.</summary>
    public JsonObject ToJson()
    {
        var data = new JsonObject
        {
            ["clone"] = Repo,
            ["ref"] = Ref,
            ["sha"] = Sha,
            ["name"] = Name,
            ["subject"] = Subject,
            ["snapshot"] = Snapshot,
        };
        foreach (var (key, value) in new[] { ("branch", Trailers.Branch), ("base", Trailers.Base), ("path", Trailers.Path), ("pr", Trailers.Pr), ("at", Trailers.At), ("by", Trailers.By) })
        {
            if (value.Length > 0)
            {
                data[key] = value;
            }
        }

        return data;
    }
}

/// <summary>
/// The shelves on origin: what <c>estate shelve</c> pushed, read back, pruned and restored. A shelf's trailers say where
/// its work came from, so listing, pruning and restoring need nothing but the ref. Ported from
/// <c>src/rig/estate/shelf_store.py</c>.
/// </summary>
/// <param name="git">Runs git in the clone.</param>
public sealed class ShelfStore(Git git)
{
    /// <summary>The subject prefix every marker commit carries: the contract.</summary>
    public const string Prefix = "TEMP-SHELVED:";

    /// <summary>Where the shelves live on the remote.</summary>
    public const string RefRoot = "refs/heads/shelved";

    /// <summary>The remote shelves are pushed to.</summary>
    public const string Remote = "origin";

    /// <summary>Long enough for a push of a large tree or a slow remote; git's default call timeout is far shorter.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(300);

    /// <summary>Runs git with long paths on, so a write never stops part-way through a tree. Never throws on failure.</summary>
    public async Task<ShelfStep> RunAsync(string cwd, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment = null)
    {
        var ran = await git.RunAsync(cwd, [.. Trim.LongPaths, .. args], Timeout, environment).ConfigureAwait(false);
        return ran.Ok ? new ShelfStep(true, ran.Out.Trim()) : new ShelfStep(false, (ran.Err.Length > 0 ? ran.Err : ran.Out).Trim());
    }

    /// <summary>git's stripped stdout, or empty when it failed.</summary>
    public async Task<string> OutAsync(string cwd, params string[] args)
    {
        var step = await RunAsync(cwd, args).ConfigureAwait(false);
        return step.Ok ? step.Detail : string.Empty;
    }

    /// <summary>Reads the <c>Shelved-*</c> trailers from a commit message. Pure.</summary>
    public static ShelfTrailers ParseTrailers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon];
            if (key.StartsWith("Shelved-", StringComparison.Ordinal) && !key.Contains(' ', StringComparison.Ordinal))
            {
                found[key["Shelved-".Length..].ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
        }

        string Get(string name) => found.GetValueOrDefault(name, string.Empty);
        return new ShelfTrailers(Get("branch"), Get("base"), Get("path"), Get("pr"), Get("at"), Get("by"));
    }

    /// <summary><c>{ref: sha}</c> for every shelf on origin. One network call per clone.</summary>
    /// <exception cref="ShelfStoreException">origin could not be listed.</exception>
    public async Task<Dictionary<string, string>> RemoteShelvesAsync(string clone)
    {
        var listed = await RunAsync(clone, ["ls-remote", Remote, $"{RefRoot}/*"]).ConfigureAwait(false);
        if (!listed.Ok)
        {
            throw new ShelfStoreException($"could not list shelves on {Remote} for {clone}: {listed.Detail}");
        }

        var shelves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in listed.Detail.Split('\n'))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab >= 0 && tab + 1 < line.Length)
            {
                shelves[line[(tab + 1)..].Trim()] = line[..tab].Trim();
            }
        }

        return shelves;
    }

    /// <summary>Every shelf on origin for <paramref name="clone"/>, with its trailers, in ref order.</summary>
    /// <exception cref="ShelfStoreException">origin could not be listed.</exception>
    public async Task<IReadOnlyList<Shelf>> ListAsync(string clone, bool fetch = true)
    {
        var shelves = await RemoteShelvesAsync(clone).ConfigureAwait(false);
        if (fetch && shelves.Count > 0)
        {
            _ = await RunAsync(clone, ["fetch", "-q", Remote, $"+{RefRoot}/*:refs/remotes/{Remote}/shelved/*"]).ConfigureAwait(false);
        }

        var found = new List<Shelf>();
        foreach (var (reference, sha) in shelves.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var body = await OutAsync(clone, "log", "-1", "--format=%B", sha).ConfigureAwait(false);
            var subject = body.Length > 0 ? body.ReplaceLineEndings("\n").Split('\n')[0] : string.Empty;
            var tree = await OutAsync(clone, "rev-parse", $"{sha}^{{tree}}").ConfigureAwait(false);
            var parentTree = await OutAsync(clone, "rev-parse", $"{sha}^^{{tree}}").ConfigureAwait(false);
            found.Add(new Shelf(clone, reference, sha, subject, ParseTrailers(body), tree.Length > 0 && tree != parentTree));
        }

        return found;
    }

    /// <summary>
    /// Why a shelf can go, or empty to keep it. <paramref name="mergedPrs"/> maps each merged PR to the head commit it
    /// merged; a merged PR counts only when that head holds the shelf's base, because commits made after the last push
    /// were never in the PR and a squash leaves no other trace. A snapshot is never prunable: uncommitted work outlives a
    /// merged PR as often as not.
    /// </summary>
    public async Task<string> PrunableAsync(Shelf shelf, IReadOnlyDictionary<int, string> mergedPrs, string defaultBranch = "main")
    {
        ArgumentNullException.ThrowIfNull(shelf);
        ArgumentNullException.ThrowIfNull(mergedPrs);
        if (!shelf.Subject.StartsWith(Prefix, StringComparison.Ordinal) || shelf.Snapshot)
        {
            return string.Empty;
        }

        var pr = shelf.Trailers.Pr.TrimStart('#');
        var baseSha = shelf.Trailers.Base;
        var mergedHead = pr.Length > 0 && pr.All(char.IsAsciiDigit) && int.TryParse(pr, out var number)
            ? mergedPrs.GetValueOrDefault(number, string.Empty)
            : string.Empty;
        if (baseSha.Length > 0 && mergedHead.Length > 0
            && (baseSha == mergedHead || (await RunAsync(shelf.Repo, ["merge-base", "--is-ancestor", baseSha, mergedHead]).ConfigureAwait(false)).Ok))
        {
            return $"PR #{pr} merged";
        }

        if (baseSha.Length > 0 && (await RunAsync(shelf.Repo, ["merge-base", "--is-ancestor", baseSha, $"{Remote}/{defaultBranch}"]).ConfigureAwait(false)).Ok)
        {
            return $"base is in {defaultBranch}";
        }

        return string.Empty;
    }

    /// <summary>Deletes a shelf from the remote, only if it still points where it was read.</summary>
    public Task<ShelfStep> DropAsync(Shelf shelf)
    {
        ArgumentNullException.ThrowIfNull(shelf);
        return RunAsync(shelf.Repo, ["push", "-q", $"--force-with-lease={shelf.Ref}:{shelf.Sha}", Remote, $":{shelf.Ref}"]);
    }

    /// <summary>
    /// Recreates the worktree and puts a snapshot back as unstaged changes, deletions included. The detail is the
    /// worktree's path. An existing local branch is only fast-forwarded: anything else would discard commits.
    /// </summary>
    public async Task<ShelfStep> UnshelveAsync(Shelf shelf, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(shelf);
        var clone = shelf.Repo;
        if (!shelf.Subject.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new ShelfStep(false, $"{shelf.Ref} tip is not a {Prefix} commit; refusing to guess");
        }

        var target = path ?? Path.Combine(clone, (shelf.Trailers.Path.Length > 0 ? shelf.Trailers.Path : $".worktrees/{shelf.Name}").Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(target) || File.Exists(target))
        {
            return new ShelfStep(false, $"{target} already exists");
        }

        var branch = shelf.Trailers.Branch;
        var baseSha = $"{shelf.Sha}^";
        ShelfStep added;
        if (branch.Length > 0 && (await OutAsync(clone, "rev-parse", "--verify", "-q", $"refs/heads/{branch}").ConfigureAwait(false)).Length > 0)
        {
            if (!(await RunAsync(clone, ["merge-base", "--is-ancestor", branch, baseSha]).ConfigureAwait(false)).Ok)
            {
                return new ShelfStep(false, $"local branch {branch} has commits the shelf lacks; restore with --path and a new branch");
            }

            _ = await RunAsync(clone, ["branch", "-f", branch, baseSha]).ConfigureAwait(false);
            added = await RunAsync(clone, ["worktree", "add", target, branch]).ConfigureAwait(false);
        }
        else if (branch.Length > 0)
        {
            added = await RunAsync(clone, ["worktree", "add", "-b", branch, target, baseSha]).ConfigureAwait(false);
        }
        else
        {
            added = await RunAsync(clone, ["worktree", "add", "--detach", target, baseSha]).ConfigureAwait(false);
        }

        if (!added.Ok)
        {
            return new ShelfStep(false, $"worktree add: {added.Detail}");
        }

        if (shelf.Snapshot)
        {
            // Check the snapshot's files out over the base without moving HEAD, then unstage them.
            var restored = await RunAsync(target, ["read-tree", "-u", "--reset", shelf.Sha]).ConfigureAwait(false);
            if (restored.Ok)
            {
                restored = await RunAsync(target, ["reset", "-q", "--mixed", "HEAD"]).ConfigureAwait(false);
            }

            if (!restored.Ok)
            {
                return new ShelfStep(false, $"restoring snapshot into {target}: {restored.Detail}");
            }
        }

        return new ShelfStep(true, target);
    }
}
