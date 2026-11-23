using Rig.Core.Devenv;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Devenv;

/// <summary>Which catalog packages brew and winget can upgrade, read from captured output of each.</summary>
public sealed class OutdatedPackagesTests
{
    // brew outdated --json=v2, trimmed from a real run: formulae and casks, one of each not in the catalog.
    private const string BrewJson = """
        {
          "formulae": [
            {"name": "gh", "installed_versions": ["2.100.0", "2.101.0"], "current_version": "2.105.0", "pinned": false, "pinned_version": null},
            {"name": "fnm", "installed_versions": ["1.38.1"], "current_version": "1.39.0", "pinned": false, "pinned_version": null},
            {"name": "cairo", "installed_versions": ["1.18.4"], "current_version": "1.18.6", "pinned": false, "pinned_version": null},
            {"installed_versions": ["1"], "current_version": "2"}
          ],
          "casks": [
            {"name": "visual-studio-code", "installed_versions": ["1.105.0"], "current_version": "1.106.1"},
            {"name": "1password-cli", "installed_versions": ["2.40.0"], "current_version": "2.42.0"}
          ]
        }
        """;

    // winget upgrade as captured on Windows: spinner frames before the header, a "<" version and the footer.
    private const string WingetTable =
        "\r   - \r   \\ \r                                                                                                                        \r"
        + "Name                         Id                       Version      Available    Source\n"
        + "-----------------------------------------------------------------------------------------\n"
        + "GitHub CLI                   GitHub.cli               2.101.0      2.105.0      winget\r\n"
        + "Microsoft Visual Studio Code Microsoft.VisualStudioCode < 1.105.0  1.106.1      winget\n"
        + "Some Other App               Other.App                1.0          2.0          winget\n"
        + "Truncated Row                Git.Git\n"
        + "3 upgrades available.\n";

    private static readonly Ran Done = new(0, string.Empty, string.Empty);

    private readonly FakeProc _proc = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DevTool Tool(string id, string? brew, string? winget = null) =>
        new(id, id, string.Empty, "test", new ToolInstall(winget, brew, null), Required: false);

    [Fact]
    public void Brew_reports_catalog_formulae_and_casks_by_short_name_with_the_newest_installed_version() =>
        Assert.Equal(
            [new OutdatedPackage("gh", "2.101.0", "2.105.0"), new OutdatedPackage("jandedobbeleer/oh-my-posh/fnm", "1.38.1", "1.39.0"), new OutdatedPackage("visual-studio-code", "1.105.0", "1.106.1")],
            Homebrew.ParseOutdated(BrewJson, ["gh", "jandedobbeleer/oh-my-posh/fnm", "visual-studio-code", "ripgrep"]));

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"formulae": [{"name": 3}]}""")]
    public void Brew_output_that_is_not_its_json_is_unknown(string json) =>
        Assert.Null(Homebrew.ParseOutdated(json, ["gh"]));

    [Fact]
    public void Brew_fills_a_missing_version_with_a_question_mark() =>
        Assert.Equal([new OutdatedPackage("gh", "?", "?")], Homebrew.ParseOutdated("""{"formulae": [{"name": "gh"}]}""", ["gh"]));

    [Fact]
    public async Task Brew_asks_once_and_a_failure_is_unknown()
    {
        _ = _proc.Answer(new Ran(0, BrewJson, string.Empty), "brew", "outdated");
        var brew = new Homebrew(_proc);

        Assert.Equal([new OutdatedPackage("gh", "2.101.0", "2.105.0")], await brew.OutdatedAsync(["gh", "jq"], Token));
        Assert.Equal(["brew", "outdated", "--json=v2"], _proc.Requests.Single().Argv);
        Assert.False(_proc.Requests.Single().Attached);

        _ = _proc.Answer(new Ran(1, string.Empty, "Error: no network"), "brew", "outdated");
        Assert.Null(await brew.OutdatedAsync(["gh"], Token));
    }

    [Fact]
    public void Winget_rows_are_found_by_their_id_and_keep_an_unknown_version_whole() =>
        Assert.Equal(
            [new OutdatedPackage("GitHub.cli", "2.101.0", "2.105.0"), new OutdatedPackage("Microsoft.VisualStudioCode", "< 1.105.0", "1.106.1")],
            Winget.ParseUpgrades(WingetTable, ["GitHub.cli", "Microsoft.VisualStudioCode", "Git.Git", "BurntSushi.ripgrep.MSVC"]));

    [Fact]
    public void Winget_reports_the_catalog_spelling_of_an_id() =>
        Assert.Equal("github.CLI", Winget.ParseUpgrades(WingetTable, ["github.CLI"]).Single().Id);

    [Fact]
    public async Task Winget_asks_without_prompting_and_reads_a_table_even_from_a_non_zero_exit()
    {
        _ = _proc.Answer(new Ran(0, WingetTable, string.Empty), "winget", "upgrade");
        var winget = new Winget(_proc);

        Assert.Single((await winget.OutdatedAsync(["GitHub.cli"], Token))!);
        Assert.Equal(["winget", "upgrade", "--accept-source-agreements", "--disable-interactivity"], _proc.Requests.Single().Argv);

        _ = _proc.Answer(new Ran(-1978335212, "Name Id Version Available Source\n------\nNo applicable upgrade found.\n", string.Empty), "winget", "upgrade");
        Assert.Empty((await winget.OutdatedAsync(["GitHub.cli"], Token))!);

        _ = _proc.Answer(new Ran(1, "Failed when searching source: winget\n", string.Empty), "winget", "upgrade");
        Assert.Null(await winget.OutdatedAsync(["GitHub.cli"], Token));
    }

    [Fact]
    public async Task The_catalog_asks_about_each_packaged_tool_once()
    {
        _ = _proc.Answer(new Ran(0, BrewJson, string.Empty), "brew", "outdated");
        var tools = new DevTools(new Homebrew(_proc), _ => null);

        var outdated = await tools.OutdatedAsync([Tool("gh", "gh"), Tool("cursor", "visual-studio-code"), Tool("vscode", "visual-studio-code"), Tool("agy", null), Tool("jq", "jq")], Token);

        Assert.NotNull(outdated);
        Assert.Equal(["gh", "cursor", "vscode"], outdated.Select(entry => entry.Tool.Id));
        Assert.Equal("1.106.1", outdated[1].Package.Available);
        Assert.Single(_proc.Requests);
    }

    [Fact]
    public async Task The_catalog_cannot_say_without_a_manager_or_when_it_fails()
    {
        Assert.Null(await new DevTools(null, _ => null).OutdatedAsync([Tool("gh", "gh")], Token));

        _ = _proc.Answer(Done with { Code = 1 }, "brew");
        Assert.Null(await new DevTools(new Homebrew(_proc), _ => null).OutdatedAsync([Tool("gh", "gh")], Token));
    }
}
