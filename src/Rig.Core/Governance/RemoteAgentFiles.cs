using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>One redundant agent file on a repository's default branch.</summary>
/// <param name="Repo">The repository, <c>owner/name</c>.</param>
/// <param name="Path">The file, or the folder for a folder entry.</param>
/// <param name="Archived">Whether the repository is archived: reported, never gated.</param>
public sealed record RemoteAgentFile(string Repo, string Path, bool Archived)
{
    /// <summary>The row the <c>--json</c> report prints.</summary>
    public JsonObject ToJson() => new() { ["repo"] = Repo, ["path"] = Path, ["archived"] = Archived };
}

/// <summary>
/// What the GitHub sweep found, and what it could not read: each owner whose repositories could not be listed and each
/// repository whose tree could not be read, so the audit fails rather than claiming coverage it does not have.
/// </summary>
/// <param name="Findings">Redundant files, sorted by repository then path.</param>
/// <param name="Unread">Owners and repositories that could not be read.</param>
public sealed record RemoteSweep(IReadOnlyList<RemoteAgentFile> Findings, IReadOnlyList<string> Unread)
{
    /// <summary>A sweep that did not run.</summary>
    public static RemoteSweep None { get; } = new([], []);
}

/// <summary>
/// Sweeps the default branch of every repository the estate's owners own for agent files that shadow
/// <c>AGENTS.md</c>, over the GitHub API, so a repository with no local clone is covered too. Ported from
/// <c>rig.repo_governance.agent_files_remote</c>.
/// </summary>
/// <param name="proc">Runs <c>gh</c>.</param>
public sealed class RemoteAgentFiles(IProc proc)
{
    /// <summary>Instruction files removed before the bespoke list existed, still swept for.</summary>
    public static readonly IReadOnlyList<string> LegacyInstructionFiles = ["CLAUDE.md"];

    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The paths among <paramref name="paths"/> a tool reads instead of <c>AGENTS.md</c>. A folder entry matches any file
    /// beneath it and is reported once, by its name.
    /// </summary>
    public static IReadOnlyList<string> FindRedundant(IEnumerable<string> paths)
    {
        var present = paths.ToHashSet(StringComparer.Ordinal);
        var found = LegacyInstructionFiles.Where(present.Contains).ToList();
        foreach (var relative in BespokeAgentFiles.Defaults)
        {
            if (relative.EndsWith('/'))
            {
                var stripped = relative.TrimEnd('/');
                if (present.Any(path => path.StartsWith(stripped + "/", StringComparison.Ordinal)))
                {
                    found.Add(stripped);
                }
            }
            else if (present.Contains(relative))
            {
                found.Add(relative);
            }
        }

        return found;
    }

    /// <summary>
    /// Every redundant agent file on the default branch of every repository <paramref name="owners"/> own. A fork's
    /// files are upstream's, so a fork is skipped unless <paramref name="isRegistered"/> says a policy names it.
    /// </summary>
    public async Task<RemoteSweep> SweepAsync(IReadOnlyList<string> owners, Func<string, bool> isRegistered, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(isRegistered);
        var findings = new List<RemoteAgentFile>();
        var unread = new List<string>();
        foreach (var owner in owners)
        {
            var repos = await ListOwnerReposAsync(owner, cancellationToken).ConfigureAwait(false);
            if (repos is null)
            {
                unread.Add(owner);
                continue;
            }

            foreach (var repo in repos)
            {
                if (repo.IsFork && !isRegistered(repo.NameWithOwner))
                {
                    continue;
                }

                if (repo.Branch.Length == 0)
                {
                    continue;
                }

                var paths = await DefaultBranchPathsAsync(repo.NameWithOwner, repo.Branch, cancellationToken).ConfigureAwait(false);
                if (paths is null)
                {
                    unread.Add(repo.NameWithOwner);
                    continue;
                }

                findings.AddRange(FindRedundant(paths).Select(path => new RemoteAgentFile(repo.NameWithOwner, path, repo.IsArchived)));
            }
        }

        return new([.. findings.OrderBy(f => f.Repo, StringComparer.Ordinal).ThenBy(f => f.Path, StringComparer.Ordinal)], unread);
    }

    private async Task<List<OwnerRepo>?> ListOwnerReposAsync(string owner, CancellationToken cancellationToken)
    {
        if (await GhJsonAsync(["repo", "list", owner, "--limit", "200", "--json", "nameWithOwner,isArchived,isFork,defaultBranchRef"], cancellationToken).ConfigureAwait(false) is not JsonArray rows)
        {
            return null;
        }

        var repos = new List<OwnerRepo>();
        foreach (var row in rows)
        {
            if (row is not JsonObject repo || Str(repo["nameWithOwner"]) is not { } name
                || Flag(repo, "isArchived") is not { } archived || Flag(repo, "isFork") is not { } fork)
            {
                return null;
            }

            var branch = string.Empty;
            switch (repo["defaultBranchRef"])
            {
                case null:
                    break;
                case JsonObject reference when !reference.ContainsKey("name"):
                    break;
                case JsonObject reference when Str(reference["name"]) is { } named:
                    branch = named;
                    break;
                default:
                    return null;
            }

            repos.Add(new(name, archived, fork, branch));
        }

        return repos;
    }

    private async Task<HashSet<string>?> DefaultBranchPathsAsync(string repo, string branch, CancellationToken cancellationToken)
    {
        if (await GhJsonAsync(["api", $"repos/{repo}/git/trees/{branch}?recursive=1"], cancellationToken).ConfigureAwait(false) is not JsonObject tree)
        {
            return null;
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (!tree.TryGetPropertyValue("tree", out var entries))
        {
            return paths;
        }

        if (entries is not JsonArray array)
        {
            return null;
        }

        foreach (var entry in array)
        {
            if (entry is not JsonObject item)
            {
                return null;
            }

            if (!item.TryGetPropertyValue("path", out var value))
            {
                continue;
            }

            if (Str(value) is not { } path)
            {
                return null;
            }

            if (path.Length > 0)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    // gh's stdout decoded, or null when it fails, prints nothing, or prints something that is not JSON.
    private async Task<JsonNode?> GhJsonAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var ran = await proc.RunAsync(new ProcRequest(["gh", .. args]) { Timeout = GhTimeout }, cancellationToken).ConfigureAwait(false);
        if (!ran.Ok || string.IsNullOrWhiteSpace(ran.Out))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(ran.Out);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) =>
        node is not null && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    // A boolean field, false when absent; null when present but not a boolean.
    private static bool? Flag(JsonObject node, string name) =>
        !node.TryGetPropertyValue(name, out var value) ? false
        : value?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    private sealed record OwnerRepo(string NameWithOwner, bool IsArchived, bool IsFork, string Branch);
}
