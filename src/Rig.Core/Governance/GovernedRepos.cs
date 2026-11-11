using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Governance;

/// <summary>
/// Which local checkouts repo governance acts on. Code merely cloned is out of scope: whether a checkout is a fork,
/// and whether you may push to it, comes from GitHub, and a profile naming a repo opts it in. Ported from
/// <c>rig.repo_governance.repo_discovery</c>.
/// </summary>
/// <param name="proc">Runs <c>gh</c>.</param>
/// <param name="git">Runs git, for each checkout's origin.</param>
/// <param name="scopes">The estate's org folders.</param>
/// <param name="policies">The policy, for selection rules and registrations.</param>
public sealed class GovernedRepos(IProc proc, Git git, EstateScopes scopes, RepoPolicies policies)
{
    private static readonly TimeSpan IdentityTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The clones in <paramref name="personalDir"/> and the org folders beside it, each folder's sorted by name.</summary>
    public IReadOnlyList<string> EstateClones(string personalDir)
    {
        var clones = new List<string>();
        foreach (var parent in scopes.RepoParentDirs(personalDir).Where(DocsTree.IsDir))
        {
            clones.AddRange(DocsTree.List(parent)
                .Order(DocsTree.Order)
                .Where(child => DocsTree.IsDir(child) && DocsTree.Exists(Path.Join(child, ".git"))));
        }

        return clones;
    }

    /// <summary>Whether <paramref name="repoPath"/> is in scope, and why not when it is not.</summary>
    public async Task<ManageDecision> IsManagedAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var origin = (await git.RunAsync(repoPath, ["remote", "get-url", "origin"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        var view = await proc.RunAsync(
            new ProcRequest(["gh", "repo", "view", .. origin.Length > 0 ? [origin] : Array.Empty<string>(), "--json", "nameWithOwner,viewerPermission,isFork"])
            {
                WorkingDirectory = repoPath,
                Timeout = IdentityTimeout,
            },
            cancellationToken).ConfigureAwait(false);
        if (Identity(view) is not var (permission, isFork))
        {
            return new(false, "could not resolve origin");
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
        return RepoSelection.ShouldManage(policies.SelectionRules(name), policies.IsRegistered(name), isFork, permission);
    }

    /// <summary>
    /// The checkouts a command acts on: with <paramref name="all"/>, every managed estate clone; else
    /// <paramref name="path"/> resolved against <paramref name="cwd"/> (none when it does not exist); else
    /// <paramref name="cwd"/> itself.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveTargetsAsync(string? path, bool all, string personalDir, string cwd, CancellationToken cancellationToken = default)
    {
        if (all)
        {
            var targets = new List<string>();
            foreach (var clone in EstateClones(personalDir))
            {
                if ((await IsManagedAsync(clone, cancellationToken).ConfigureAwait(false)).Managed)
                {
                    targets.Add(clone);
                }
            }

            return targets;
        }

        if (!string.IsNullOrEmpty(path))
        {
            var target = PathResolution.Resolve(Path.Combine(cwd, path));
            return DocsTree.Exists(target) ? [target] : [];
        }

        return [cwd];
    }

    // The viewer's permission and whether the repo is a fork, from `gh repo view --json`; null when it did not answer.
    private static (string Permission, bool IsFork)? Identity(Ran view)
    {
        if (!view.Ok || string.IsNullOrWhiteSpace(view.Out))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(view.Out) is JsonObject fields
                && fields["nameWithOwner"] is { } name && name.GetValueKind() == JsonValueKind.String
                && fields["viewerPermission"] is { } permission && permission.GetValueKind() == JsonValueKind.String
                && fields["isFork"] is { } fork && fork.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                ? (permission.GetValue<string>(), fork.GetValue<bool>())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
