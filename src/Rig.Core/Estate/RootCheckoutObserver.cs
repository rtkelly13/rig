using System.Globalization;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>
/// Reads one root checkout's fast-forward facts through <see cref="Git"/>.
/// </summary>
/// <remarks>
/// The read is shaped like <c>OrcaBaseRef</c> (#391): the branch, its configured upstream and the ahead/behind track
/// come from one <c>git status --porcelain=v2 --branch</c> call, and the <c>rev-list</c> comparison is a fallback for a
/// branch whose upstream is not the remote default. Fetching updates remote-tracking refs; no working tree is touched.
/// </remarks>
/// <param name="git">Runs git, hardened under untrusted folders.</param>
internal sealed class RootCheckoutObserver(Git git)
{
    /// <summary>How long one fetch may take.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The fallback remote default when <c>origin/HEAD</c> is absent.</summary>
    public const string FallbackDefaultBranch = "main";

    /// <summary>Reads everything the planner needs about <paramref name="repo"/>.</summary>
    /// <param name="repo">The candidate root checkout.</param>
    /// <param name="refreshRemote">Fetch <c>origin</c> before comparing its default branch.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    public async Task<RootCheckoutFacts> ObserveAsync(string repo, bool refreshRemote, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repo))
        {
            return new RootCheckoutFacts(repo, Exists: false, Error: "folder not found");
        }

        if (git.IsUntrusted(repo))
        {
            return new RootCheckoutFacts(repo, IsUntrusted: true);
        }

        var resolved = PathResolution.Resolve(repo);
        var (isRoot, gitDir, layoutError) = await LayoutAsync(resolved, cancellationToken).ConfigureAwait(false);
        if (layoutError.Length > 0)
        {
            return new RootCheckoutFacts(resolved, IsRootCheckout: false, Error: layoutError);
        }

        if (!isRoot)
        {
            var (linked, linkedError) = await StatusAsync(resolved, cancellationToken).ConfigureAwait(false);
            return linkedError.Length > 0
                ? new RootCheckoutFacts(resolved, IsRootCheckout: false, Error: linkedError)
                : new RootCheckoutFacts(
                    resolved,
                    IsRootCheckout: false,
                    IsDirty: linked.Dirty,
                    IsDetached: linked.Detached,
                    Branch: linked.Branch,
                    Operation: OperationInProgress(gitDir));
        }

        var origin = await git.RunAsync(resolved, ["remote", "get-url", "origin"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var hasOrigin = origin.Ok;
        if (hasOrigin && refreshRemote)
        {
            var fetch = await git.RunAsync(
                resolved,
                ["fetch", "--prune", "--quiet", "origin"],
                FetchTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!fetch.Ok)
            {
                return new RootCheckoutFacts(resolved, Error: $"git fetch --prune origin failed: {LastLine(fetch.Err) ?? "no output"}");
            }
        }

        // Read after the fetch so branch.ab is the remote's state now, not the last time it was fetched.
        var (state, statusError) = await StatusAsync(resolved, cancellationToken).ConfigureAwait(false);
        if (statusError.Length > 0)
        {
            return new RootCheckoutFacts(resolved, Error: statusError);
        }

        var operation = OperationInProgress(gitDir);
        if (state.Detached)
        {
            return new RootCheckoutFacts(
                resolved,
                IsDirty: state.Dirty,
                IsDetached: true,
                Branch: state.Branch,
                Operation: operation);
        }

        var defaultBranch = await DefaultBranchAsync(resolved, cancellationToken).ConfigureAwait(false);
        var target = $"origin/{defaultBranch}";
        var (hasTarget, ahead, behind, trackError) = await TrackAsync(resolved, state, target, hasOrigin, cancellationToken).ConfigureAwait(false);
        if (trackError.Length > 0)
        {
            return new RootCheckoutFacts(
                resolved,
                IsDirty: state.Dirty,
                Branch: state.Branch,
                DefaultBranch: defaultBranch,
                Target: target,
                HasRemote: true,
                Operation: operation,
                Error: trackError);
        }

        return new RootCheckoutFacts(
            resolved,
            IsRootCheckout: true,
            IsDirty: state.Dirty,
            Branch: state.Branch,
            DefaultBranch: defaultBranch,
            Target: target,
            HasRemote: hasTarget,
            Ahead: ahead,
            Behind: behind,
            Operation: operation);
    }

    private async Task<(CheckoutState State, string Error)> StatusAsync(string repo, CancellationToken cancellationToken)
    {
        var status = await git.RunAsync(
            repo,
            ["status", "--porcelain=v2", "--branch", "--untracked-files=normal"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return status.Ok
            ? (ParseStatus(status.Out), string.Empty)
            : (new CheckoutState(false, false, string.Empty, string.Empty, false, 0, 0), LastLine(status.Err) ?? "git status failed");
    }

    private async Task<(bool IsRoot, string GitDir, string Error)> LayoutAsync(string repo, CancellationToken cancellationToken)
    {
        var paths = await git.RunAsync(
            repo,
            ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir", "--show-toplevel"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!paths.Ok)
        {
            return (false, string.Empty, LastLine(paths.Err) ?? "not a git checkout");
        }

        var lines = paths.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3)
        {
            return (false, string.Empty, "git rev-parse did not name the checkout");
        }

        var gitDir = PathResolution.Resolve(lines[0]);
        var commonDir = PathResolution.Resolve(lines[1]);
        var top = PathResolution.Resolve(lines[2]);
        return (PathResolution.Same(gitDir, commonDir) && PathResolution.Same(top, repo), gitDir, string.Empty);
    }

    private async Task<string> DefaultBranchAsync(string repo, CancellationToken cancellationToken)
    {
        var head = await git.RunAsync(
            repo,
            ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var symbolic = head.OutOrEmpty;
        return symbolic.StartsWith("origin/", StringComparison.Ordinal)
            ? symbolic["origin/".Length..]
            : FallbackDefaultBranch;
    }

    // One status call answers ahead/behind when the branch tracks the remote default; only another upstream, or no
    // upstream, pays for the rev-list comparison.
    private async Task<(bool HasTarget, int Ahead, int Behind, string Error)> TrackAsync(
        string repo,
        CheckoutState state,
        string target,
        bool hasOrigin,
        CancellationToken cancellationToken)
    {
        if (!hasOrigin)
        {
            return (false, 0, 0, string.Empty);
        }

        if (state.Upstream == target && state.HasTrack)
        {
            return (true, state.Ahead, state.Behind, string.Empty);
        }

        var verify = await git.RunAsync(
            repo,
            ["rev-parse", "--verify", "--quiet", $"refs/remotes/{target}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!verify.Ok || verify.OutOrEmpty.Length == 0)
        {
            return (false, 0, 0, string.Empty);
        }

        var counts = await git.RunAsync(
            repo,
            ["rev-list", "--left-right", "--count", $"HEAD...{target}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!counts.Ok)
        {
            return (true, 0, 0, LastLine(counts.Err) ?? $"cannot compare HEAD with {target}");
        }

        var split = counts.OutOrEmpty.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _ = int.TryParse(split.ElementAtOrDefault(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ahead);
        _ = int.TryParse(split.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var behind);
        return (true, ahead, behind, string.Empty);
    }

    private static CheckoutState ParseStatus(string porcelain)
    {
        var dirty = false;
        var detached = false;
        var branch = string.Empty;
        var upstream = string.Empty;
        var hasTrack = false;
        var ahead = 0;
        var behind = 0;
        foreach (var line in porcelain.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] != '#')
            {
                dirty = true;
                continue;
            }

            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var name = line["# branch.head ".Length..].Trim();
                detached = name == "(detached)";
                branch = detached ? string.Empty : name;
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = line["# branch.upstream ".Length..].Trim();
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && parts[0].Length > 1
                    && parts[1].Length > 1
                    && parts[0][0] == '+'
                    && parts[1][0] == '-'
                    && int.TryParse(parts[0].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAhead)
                    && int.TryParse(parts[1].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBehind))
                {
                    ahead = parsedAhead;
                    behind = parsedBehind;
                    hasTrack = true;
                }
            }
        }

        return new CheckoutState(dirty, detached, branch, upstream, hasTrack, ahead, behind);
    }

    private static string OperationInProgress(string gitDir)
    {
        if (Directory.Exists(Path.Join(gitDir, "rebase-merge")))
        {
            return "rebase";
        }

        if (Directory.Exists(Path.Join(gitDir, "rebase-apply")))
        {
            return "rebase";
        }

        if (File.Exists(Path.Join(gitDir, "MERGE_HEAD")))
        {
            return "merge";
        }

        if (File.Exists(Path.Join(gitDir, "CHERRY_PICK_HEAD")))
        {
            return "cherry-pick";
        }

        if (File.Exists(Path.Join(gitDir, "REVERT_HEAD")))
        {
            return "revert";
        }

        return File.Exists(Path.Join(gitDir, "BISECT_LOG")) ? "bisect" : string.Empty;
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private sealed record CheckoutState(
        bool Dirty,
        bool Detached,
        string Branch,
        string Upstream,
        bool HasTrack,
        int Ahead,
        int Behind);
}
