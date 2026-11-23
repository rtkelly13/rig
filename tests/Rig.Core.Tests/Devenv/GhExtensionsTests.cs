using Rig.Core.Devenv;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Devenv;

/// <summary>The required gh extensions, <c>gh extension list</c>'s rows, and the latest-release comparison.</summary>
public sealed class GhExtensionsTests
{
    // gh extension list as gh 2.101 prints it: name, repository and version, tab-separated.
    private const string Listed = "gh aw\tgithub/gh-aw\tv0.77.5\ngh stack\tgithub/gh-stack\tv0.1.0\n";

    private readonly FakeProc _proc = new();
    private readonly FakeGitHub _github = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private GhExtensionManager Manager => new(_proc, _github);

    private static InstalledGhExtension Stack(string version) => new("gh stack", "github/gh-stack", version);

    [Fact]
    public void The_bundled_list_requires_gh_stack_once_with_a_rationale()
    {
        Assert.Contains(GhExtensions.Required, extension => extension.Repo == "github/gh-stack");
        Assert.Equal(GhExtensions.Required.Count, GhExtensions.Required.Select(e => e.Repo).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(GhExtensions.Required, extension => Assert.False(string.IsNullOrWhiteSpace(extension.Rationale)));
    }

    [Fact]
    public void A_document_lists_each_required_repo_in_order() =>
        Assert.Equal(
            [new GhExtension("a/gh-one", "first"), new GhExtension("b/gh-two", "second")],
            GhExtensions.Parse("""{"required": [{"repo": "a/gh-one", "rationale": "first"}, {"repo": "b/gh-two", "rationale": "second"}]}"""));

    [Theory]
    [InlineData("{not json", "gh-extensions.json is not JSON: ")]
    [InlineData("""{"required": {}}""", "gh-extensions.json: $.required must be an array.")]
    [InlineData("""[]""", "gh-extensions.json: $.required must be an array.")]
    [InlineData("""{"required": [{"rationale": "x"}]}""", "gh-extensions.json: $.required[0].repo must be a non-empty string.")]
    [InlineData("""{"required": ["a/b"]}""", "gh-extensions.json: $.required[0].repo must be a non-empty string.")]
    [InlineData("""{"required": [{"repo": "a/b", "rationale": 3}]}""", "gh-extensions.json: $.required[0].rationale must be a non-empty string.")]
    [InlineData("""{"required": [{"repo": "a/b", "rationale": "x"}, {"repo": "", "rationale": "x"}]}""", "gh-extensions.json: $.required[1].repo must be a non-empty string.")]
    public void A_misshapen_document_names_where_it_is_wrong(string text, string message) =>
        Assert.StartsWith(message, Assert.Throws<InvalidDataException>(() => GhExtensions.Parse(text)).Message, StringComparison.Ordinal);

    [Fact]
    public void The_list_is_read_row_by_row()
    {
        Assert.Equal(
            [new InstalledGhExtension("gh aw", "github/gh-aw", "v0.77.5"), Stack("v0.1.0")],
            GhExtensions.ParseList(Listed));
        Assert.Equal(
            [new InstalledGhExtension("gh local", "owner/gh-local", string.Empty), new InstalledGhExtension("gh src", "o/gh-src", "0abc123")],
            GhExtensions.ParseList("gh local\towner/gh-local\r\n\nnot a row\ngh src\to/gh-src\t0abc123\r\ngh blank\t \tv1\n"));
        Assert.Empty(GhExtensions.ParseList(string.Empty));
    }

    [Fact]
    public void Missing_matches_repositories_ignoring_case()
    {
        Assert.Empty(GhExtensions.Missing([new InstalledGhExtension("gh stack", "GitHub/GH-Stack", "v0.3.0")]));
        Assert.Equal(GhExtensions.Required, GhExtensions.Missing([new InstalledGhExtension("gh aw", "github/gh-aw", "v1")]));
    }

    [Theory]
    [InlineData("v0.3.0", true)]
    [InlineData("1.2", true)]
    [InlineData("V2", true)]
    [InlineData("v1.0.0-rc.1", true)]
    [InlineData("0abc123", false)]
    [InlineData("", false)]
    [InlineData("main", false)]
    public void A_tag_is_told_from_a_commit(string version, bool tag)
    {
        Assert.Equal(tag, GhExtensions.IsTag(version));
        Assert.Equal(tag, Stack(version).Tagged);
    }

    [Theory]
    [InlineData("v0.3.0", "0.3.0", true)]
    [InlineData("v0.3.0", "V0.3.0", true)]
    [InlineData("v0.1.0", "v0.3.0", false)]
    public void Versions_compare_without_the_v(string installed, string latest, bool same) =>
        Assert.Equal(same, GhExtensions.SameVersion(installed, latest));

    [Fact]
    public async Task Listing_reads_gh_or_reports_its_error()
    {
        _ = _proc.Answer(new Ran(0, Listed, string.Empty), "gh", "extension", "list");

        var (installed, error) = await Manager.ListAsync(Token);

        Assert.Equal(2, installed!.Count);
        Assert.Empty(error);

        _ = _proc.Answer(new Ran(4, string.Empty, "warning\nauthentication required\n"), "gh", "extension", "list");
        Assert.Equal((null, "authentication required"), await Manager.ListAsync(Token));

        _ = _proc.Answer(new Ran(3, string.Empty, string.Empty), "gh", "extension", "list");
        Assert.Equal("gh exited 3", (await Manager.ListAsync(Token)).Error);
    }

    [Fact]
    public async Task Install_and_upgrade_run_gh()
    {
        _ = _proc.Answer(new Ran(0, string.Empty, string.Empty), "gh", "extension");

        Assert.Null(await Manager.InstallAsync("github/gh-stack", Token));
        Assert.True(await Manager.UpgradeAllAsync(Token));

        Assert.Equal(["gh", "extension", "install", "github/gh-stack"], _proc.Requests[0].Argv);
        Assert.False(_proc.Requests[0].Attached);
        Assert.Equal(["gh", "extension", "upgrade", "--all"], _proc.Requests[1].Argv);
        Assert.True(_proc.Requests[1].Attached);

        _ = _proc.Answer(new Ran(1, string.Empty, "X Could not find a release\n"), "gh", "extension");

        Assert.Equal("X Could not find a release", await Manager.InstallAsync("o/gh-none", Token));
        Assert.False(await Manager.UpgradeAllAsync(Token));
    }

    [Fact]
    public async Task Each_tagged_extension_is_compared_with_its_latest_release()
    {
        _ = _github
            .On("GET", "repos/github/gh-stack/releases/latest", """{"tag_name": "v0.3.0"}""")
            .On("GET", "repos/github/gh-aw/releases/latest", """{"tag_name": "0.77.5"}""");

        var releases = await Manager.ReleasesAsync([Stack("v0.1.0"), new("gh aw", "github/gh-aw", "v0.77.5")], Token);

        Assert.Equal([new GhExtensionRelease(Stack("v0.1.0"), "v0.3.0", null), new GhExtensionRelease(new("gh aw", "github/gh-aw", "v0.77.5"), "0.77.5", null)], releases);
        Assert.True(releases[0].Outdated);
        Assert.False(releases[1].Outdated);
    }

    [Fact]
    public async Task An_extension_that_cannot_be_compared_says_why_and_is_not_outdated()
    {
        _ = _github
            .On("GET", "repos/o/gh-bad/releases/latest", "{not json")
            .On("GET", "repos/o/gh-list/releases/latest", "[]")
            .On("GET", "repos/o/gh-untagged/releases/latest", """{"tag_name": ""}""");

        var releases = await Manager.ReleasesAsync(
            [
                new("gh none", "o/gh-none", "v1.0.0"),
                new("gh bad", "o/gh-bad", "v1.0.0"),
                new("gh list", "o/gh-list", "v1.0.0"),
                new("gh untagged", "o/gh-untagged", "v1.0.0"),
                new("gh src", "o/gh-src", "0abc123"),
                new("gh local", "o/gh-local", string.Empty),
            ],
            Token);

        Assert.Equal(
            [
                "could not read the latest release: gh: Not Found (HTTP 404)",
                "the latest release is not JSON",
                "the latest release has no tag_name",
                "the latest release has no tag_name",
                "installed at 0abc123, not a release",
                "installed locally, not from a release",
            ],
            releases.Select(release => release.Problem));
        Assert.All(releases, release => Assert.False(release.Outdated));
        Assert.Equal(4, _github.Calls.Count);
    }
}
