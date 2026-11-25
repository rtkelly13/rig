using System.Globalization;
using Rig.Core.Text;

namespace Rig.Core.GitHub;

/// <summary>
/// A github.com page, read from its address: which repo it belongs to and, for a pull request or an issue, which one.
/// Any subpage counts (<c>/pull/12/files</c> is PR 12), and addresses that name no repo (settings, search, an org's
/// overview) are not pages here.
/// </summary>
/// <param name="Repo">The repo, as the address spells it.</param>
public abstract record GitHubPage(RepoSlug Repo)
{
    // First path segments that GitHub reserves for its own pages, so they never name an owner.
    private static readonly HashSet<string> NotOwners = new(
        [
            "orgs", "users", "search", "settings", "notifications", "pulls", "issues", "marketplace", "sponsors", "topics",
            "login", "new", "organizations", "apps", "features", "explore", "codespaces", "collections", "trending", "about",
            "enterprise", "dashboard", "account",
        ],
        StringComparer.Ordinal);

    /// <summary>The page's canonical address; <see cref="Parse(Uri?)"/> reads it back as this page.</summary>
    public abstract Uri Url { get; }

    /// <summary>Whether <paramref name="url"/> is on github.com, whatever page it shows.</summary>
    public static bool OnGitHub(Uri? url) =>
        url is { IsAbsoluteUri: true } && url.Host.ToLowerInvariant() is "github.com" or "www.github.com";

    /// <summary>The page <paramref name="url"/> shows, or null for any address that is not a github.com repo page.</summary>
    public static GitHubPage? Parse(Uri? url) =>
        url is not null && OnGitHub(url) ? Parse(url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)) : null;

    /// <summary>The address under the repo, for the cases to build on.</summary>
    private protected Uri At(string tail) => new($"https://github.com/{Repo.FullName}{tail}");

    private protected static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static GitHubPage? Parse(string[] path) => path switch
    {
        [var owner, ..] when NotOwners.Contains(owner.ToLowerInvariant()) => null,
        [var owner, var name, ..] when RepoSlug.FromPath($"{owner}/{name}") is null => null,
        [var owner, var name, "pull", var number, ..] when Number(number) is { } n => new PullRequestPage(new(owner, name), n),
        [var owner, var name, "issues", var number, ..] when Number(number) is { } n => new IssuePage(new(owner, name), n),
        [var owner, var name, ..] => new RepoPage(new(owner, name)),
        _ => null,
    };

    private static int? Number(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
}

/// <summary>Any page of a repo that is not a specific pull request or issue: its code, lists, actions, a new PR form.</summary>
/// <param name="Repo">The repo.</param>
public sealed record RepoPage(RepoSlug Repo) : GitHubPage(Repo)
{
    /// <inheritdoc/>
    public override Uri Url => At(string.Empty);
}

/// <summary>A pull request, on any of its tabs.</summary>
/// <param name="Repo">The repo.</param>
/// <param name="Number">The PR number.</param>
public sealed record PullRequestPage(RepoSlug Repo, int Number) : GitHubPage(Repo)
{
    /// <summary>The PR as <c>owner/repo#n</c>.</summary>
    public PrRef Pr => new(Repo.Owner, Repo.Name, Number);

    /// <inheritdoc/>
    public override Uri Url => At($"/pull/{Text(Number)}");
}

/// <summary>An issue.</summary>
/// <param name="Repo">The repo.</param>
/// <param name="Number">The issue number.</param>
public sealed record IssuePage(RepoSlug Repo, int Number) : GitHubPage(Repo)
{
    /// <inheritdoc/>
    public override Uri Url => At($"/issues/{Text(Number)}");
}
