using Rig.Core.Chrome;

namespace Rig.Core.Tests.Chrome;

/// <summary>Site groups by host: the config rules and the committed config. Replaces <c>test_chrome_site_hosts</c>.</summary>
public sealed class SiteHostsTests
{
    private static string Checkout => Path.GetDirectoryName(RepoTemp.Root)!;

    private static SiteHosts Groups(string json) => SiteHosts.Parse($$"""{"groups": {{json}}}""");

    [Fact]
    public void Committed_config_loads_and_is_the_packaged_copy()
    {
        var committed = SiteHosts.Default(Checkout);

        Assert.Equal("Dev", committed.SiteFor("api.github.com"));
        Assert.Equal(committed.Domains, SiteHosts.Packaged().Domains);
    }

    [Fact]
    public void Without_a_checkout_the_packaged_copy_is_used()
    {
        using var temp = new TempDir();

        Assert.Equal(SiteHosts.Packaged().Domains, SiteHosts.Default(temp.Path).Domains);
    }

    [Fact]
    public void Longest_domain_wins()
    {
        var sites = Groups("""{"Google": ["google.com"], "AI": ["gemini.google.com"]}""");

        Assert.Equal("AI", sites.SiteFor("Gemini.Google.com"));
        Assert.Equal("Google", sites.SiteFor("mail.google.com"));
        Assert.Equal("Google", sites.SiteFor("google.com"));
        Assert.Null(sites.SiteFor("notgoogle.com"));
        Assert.Null(SiteHosts.None.SiteFor("google.com"));
    }

    [Fact]
    public void A_domain_in_two_groups_is_rejected()
    {
        var error = Assert.Throws<InvalidDataException>(() => Groups("""{"Dev": ["npmjs.com"], "Other": ["npmjs.com"]}"""));
        Assert.Contains("npmjs.com is already in Dev", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"NPMJS.com\"")]
    [InlineData("\"localhost\"")]
    [InlineData("\"https://npmjs.com\"")]
    [InlineData("\"npmjs\"")]
    [InlineData("3")]
    [InlineData("null")]
    public void Domains_must_be_lowercase_hostnames(string domain)
    {
        var error = Assert.Throws<InvalidDataException>(() => Groups($$"""{"Dev": [{{domain}}]}"""));
        Assert.Contains("not a lowercase domain", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json", "is not JSON")]
    [InlineData("[]", "needs a \"groups\" object")]
    [InlineData("""{"groups": []}""", "needs a \"groups\" object")]
    [InlineData("""{"groups": {" ": ["a.com"]}}""", "needs a title")]
    [InlineData("""{"groups": {"Dev": "a.com"}}""", "must be a list")]
    public void Malformed_configs_are_rejected(string json, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => SiteHosts.Parse(json));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }
}
