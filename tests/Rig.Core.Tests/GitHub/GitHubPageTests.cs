using Rig.Core.GitHub;

namespace Rig.Core.Tests.GitHub;

/// <summary>Reading which github.com page an address shows.</summary>
public sealed class GitHubPageTests
{
    [Theory]
    [InlineData("https://github.com/Acme-Corp/data-platform/pull/12", "pr acme-corp/data-platform#12")]
    [InlineData("https://www.github.com/acme-corp/data-platform/pull/12/files?w=1#diff", "pr acme-corp/data-platform#12")]
    [InlineData("https://github.com/acme-corp/data-platform/issues/7", "issue acme-corp/data-platform#7")]
    [InlineData("https://github.com/acme-corp/data-platform", "repo acme-corp/data-platform")]
    [InlineData("https://github.com/acme-corp/data-platform/pull/new", "repo acme-corp/data-platform")]
    [InlineData("https://github.com/acme-corp/data-platform/pull/0", "repo acme-corp/data-platform")]
    [InlineData("https://github.com/acme-corp/data-platform/issues", "repo acme-corp/data-platform")]
    [InlineData("https://github.com/acme-corp", null)]
    [InlineData("https://github.com/orgs/acme-corp/projects", null)]
    [InlineData("https://github.com/Settings/profile", null)]
    [InlineData("https://gist.github.com/acme-corp/data-platform/pull/12", null)]
    [InlineData("https://example.com/acme-corp/data-platform/pull/12", null)]
    public void Reads_the_page_an_address_shows(string address, string? expected) =>
        Assert.Equal(expected, Describe(GitHubPage.Parse(new Uri(address))));

    [Fact]
    public void Relative_and_missing_addresses_are_no_page()
    {
        Assert.Null(GitHubPage.Parse(null));
        Assert.Null(GitHubPage.Parse(new Uri("/acme-corp/data-platform/pull/12", UriKind.Relative)));
    }

    [Fact]
    public void The_repo_keeps_the_address_spelling()
    {
        var page = Assert.IsType<RepoPage>(GitHubPage.Parse(new Uri("https://github.com/Acme-Corp/Data-Platform/actions")));
        Assert.Equal("Acme-Corp/Data-Platform", page.Repo.FullName);
    }

    private static string? Describe(GitHubPage? page) => page switch
    {
        PullRequestPage pr => $"pr {pr.Pr}",
        IssuePage issue => $"issue {issue.Repo.FullName.ToLowerInvariant()}#{issue.Number}",
        RepoPage repo => $"repo {repo.Repo.FullName.ToLowerInvariant()}",
        _ => null,
    };
}
