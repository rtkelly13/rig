namespace Rig.Core.Forks;

/// <summary>
/// Merges the names and emails people committed under into one identity each: two identities are one person when they
/// share an email, or a name that is not configured as ignored. Deterministic: each person's key is the ordinally
/// smallest email or name they used.
/// </summary>
internal sealed class Identities
{
    private readonly Dictionary<string, string> parent = new(StringComparer.Ordinal);
    private readonly HashSet<string> ignored;

    private Identities(IEnumerable<string> ignoredNames) =>
        ignored = [.. ignoredNames.Select(NormaliseName)];

    /// <summary>Merges every identity in the commits and the blame.</summary>
    public static Identities Build(ForkAssessmentConfig config, ForkFacts facts)
    {
        var identities = new Identities(config.Identity.IgnoredNames);
        foreach (var (name, email) in facts.Commits.Select(c => (c.Name, c.Email)).Concat((facts.Blame ?? []).Select(b => (b.Name, b.Email))).Distinct().Order())
        {
            identities.Add(name, email);
        }

        return identities;
    }

    /// <summary>The key of the person who authored <paramref name="commit"/>.</summary>
    public string Of(CommitFact commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return Find(EmailNode(commit.Email));
    }

    /// <summary>The name a person used most, ties broken ordinally.</summary>
    public static string Name(IEnumerable<CommitFact> commits) =>
        commits.GroupBy(c => c.Name, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;

    /// <summary>Every name a person used but their most used one.</summary>
    public static IReadOnlyList<string> Aliases(IReadOnlyCollection<CommitFact> commits)
    {
        var name = Name(commits);
        return [.. commits.Select(c => c.Name).Distinct(StringComparer.Ordinal).Where(n => n != name).Order(StringComparer.Ordinal)];
    }

    private static string EmailNode(string email) => "e:" + email.Trim().ToLowerInvariant();

    private static string NormaliseName(string name) =>
        string.Join(' ', name.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void Add(string name, string email)
    {
        var emailNode = EmailNode(email);
        _ = Find(emailNode);
        var normalised = NormaliseName(name);
        if (normalised.Length > 0 && !ignored.Contains(normalised))
        {
            Union(emailNode, "n:" + normalised);
        }
    }

    private string Find(string node)
    {
        if (!parent.TryGetValue(node, out var up))
        {
            parent[node] = node;
            return node;
        }

        if (up == node)
        {
            return node;
        }

        var root = Find(up);
        parent[node] = root;
        return root;
    }

    private void Union(string a, string b)
    {
        var (ra, rb) = (Find(a), Find(b));
        if (ra != rb)
        {
            // The smaller key wins, so the result does not depend on the order identities were added.
            if (string.CompareOrdinal(ra, rb) < 0)
            {
                parent[rb] = ra;
            }
            else
            {
                parent[ra] = rb;
            }
        }
    }
}
