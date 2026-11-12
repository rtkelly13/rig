namespace Rig.Core.GitHub;

/// <summary>
/// Stack and queue chains from one repository's full open-PR list. Pure; ported from the chain half of
/// <c>rig.github_prs</c>.
/// </summary>
public static class PrChains
{
    /// <summary>The label a PR holds while it is merging, which puts it at the head of the label queue.</summary>
    public const string LockLabel = "locked-merging";

    /// <summary>The label that queues a PR to merge.</summary>
    public const string AutomergeLabel = "automerge";

    /// <summary>Stack chains, then queue chains. Only chains of two or more members.</summary>
    public static IReadOnlyList<Chain> Build(IEnumerable<OpenPr> openPrs)
    {
        var prs = openPrs.ToList();
        return [.. Stacks(prs), .. Queues(prs)];
    }

    /// <summary>
    /// PR X sits on PR Y when X's base is Y's head. One chain per bottom PR, walked breadth-first.
    /// </summary>
    /// <remarks>
    /// A stack can branch (two PRs on one head), so members are ordered by depth, then number. A fork's PR is never a
    /// base, since its head branch is not in this repository, and neither is a head two open PRs share, since which one
    /// is below would be a guess.
    /// </remarks>
    public static IReadOnlyList<Chain> Stacks(IEnumerable<OpenPr> openPrs)
    {
        var prs = openPrs.OrderBy(pr => pr.Number).ToList();
        var headCounts = prs.Where(pr => !pr.CrossRepo).CountBy(pr => pr.Head, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var byHead = new Dictionary<string, OpenPr>(StringComparer.Ordinal);
        foreach (var pr in prs.Where(pr => !pr.CrossRepo && headCounts[pr.Head] == 1))
        {
            byHead[pr.Head] = pr;
        }

        var children = new Dictionary<int, List<OpenPr>>();
        var roots = new List<OpenPr>();
        foreach (var pr in prs)
        {
            if (!byHead.TryGetValue(pr.Base, out var parent) || parent.Number == pr.Number)
            {
                roots.Add(pr);
            }
            else if (children.TryGetValue(parent.Number, out var siblings))
            {
                siblings.Add(pr);
            }
            else
            {
                children[parent.Number] = [pr];
            }
        }

        var chains = new List<Chain>();
        foreach (var root in roots)
        {
            var members = new List<int>();
            var pending = new Queue<OpenPr>([root]);
            while (pending.TryDequeue(out var node))
            {
                members.Add(node.Number);
                foreach (var child in children.GetValueOrDefault(node.Number) ?? [])
                {
                    pending.Enqueue(child);
                }
            }

            if (members.Count > 1)
            {
                chains.Add(new Chain(ChainKind.Stack, root.Base, members));
            }
        }

        return chains;
    }

    /// <summary>Per base branch: the native merge queue by position, then the label queue.</summary>
    /// <remarks>
    /// The label queue is whoever holds <see cref="LockLabel"/>, then <see cref="AutomergeLabel"/> PRs in the order that
    /// label was last added, falling back to when the PR was created. A PR in the native queue is not also label-queued.
    /// </remarks>
    public static IReadOnlyList<Chain> Queues(IEnumerable<OpenPr> openPrs)
    {
        var chains = new List<Chain>();
        foreach (var group in openPrs.GroupBy(pr => pr.Base, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var native = group.Where(pr => pr.QueuePosition is not null)
                .OrderBy(pr => pr.QueuePosition).ThenBy(pr => pr.Number).Select(pr => pr.Number).ToList();
            var labelled = group.Where(pr => pr.QueuePosition is null).ToList();
            var holders = LabelOrder(labelled.Where(pr => pr.Labels.Contains(LockLabel)), LockLabel);
            var waiting = LabelOrder(
                labelled.Where(pr => pr.Labels.Contains(AutomergeLabel) && !pr.Labels.Contains(LockLabel)), AutomergeLabel);
            foreach (var members in new[] { native, [.. holders, .. waiting] })
            {
                if (members.Count > 1)
                {
                    chains.Add(new Chain(ChainKind.Queue, group.Key, members));
                }
            }
        }

        return chains;
    }

    private static IEnumerable<int> LabelOrder(IEnumerable<OpenPr> prs, string label) =>
        prs.OrderBy(pr => pr.LabeledAt.GetValueOrDefault(label) is { Length: > 0 } at ? at : pr.CreatedAt, StringComparer.Ordinal)
            .ThenBy(pr => pr.Number)
            .Select(pr => pr.Number);
}
