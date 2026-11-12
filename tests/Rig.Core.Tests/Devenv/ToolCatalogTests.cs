using Rig.Core.Devenv;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Devenv;

/// <summary>The tool catalog, package managers and runtimes. Ported from <c>test_config.py</c>, <c>test_tool_manager.py</c> and <c>test_steps.py</c>.</summary>
public sealed class ToolCatalogTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Entry(string id, string extra = "") =>
        $$"""{"id":"{{id}}","name":"{{id}} tool","description":"d","install":{"brew":"{{id}}"}{{extra}}}""";

    [Fact]
    public void The_bundled_catalog_lists_each_id_once_with_the_ai_cursor_first()
    {
        var tools = ToolCatalog.Load(_home.Path, _home.Path);

        Assert.Equal(tools.Count, tools.Select(t => t.Id).Distinct().Count());
        Assert.Equal(new DevTool("git", "Git", "Distributed version control system", "core", new ToolInstall("Git.Git", "git", "git"), true), tools.Single(t => t.Id == "git"));
        Assert.Equal("ai", tools.Single(t => t.Id == "cursor").Category);
        Assert.Contains(tools, t => t.Id == "uv");
        Assert.Contains(tools, t => t.Id == "fnm");
    }

    [Fact]
    public async Task User_workspace_and_custom_catalogs_add_tools_and_the_first_id_wins()
    {
        var work = _home.Folder("work");
        await File.WriteAllTextAsync(_home.File(".config/devenv/tools/b.json"), $"[{Entry("zz-user")}, {Entry("git")}]", Token);
        await File.WriteAllTextAsync(_home.File(".config/devenv/tools/a.json"), $$"""{"tools":[{{Entry("zz-first", ",\"required\":true,\"category\":\"mine\"")}}]}""", Token);
        await File.WriteAllTextAsync(_home.File("work/.devenv/tools/w.json"), $"[{Entry("zz-work")}, {Entry("zz-user")}]", Token);
        await File.WriteAllTextAsync(_home.File("extra/c.json"), $"[{Entry("zz-custom")}]", Token);
        await File.WriteAllTextAsync(_home.File(".config/devenv/config.json"), """{"custom_catalogs":["~/extra/c.json","/no/such.json"]}""", Token);

        var tools = ToolCatalog.Load(_home.Path, work);

        Assert.Equal(["zz-first", "zz-user", "zz-work", "zz-custom"], tools.Where(t => t.Id.StartsWith("zz-", StringComparison.Ordinal)).Select(t => t.Id));
        Assert.Equal(new DevTool("zz-first", "zz-first tool", "d", "mine", new ToolInstall(null, "zz-first", null), true), tools.Single(t => t.Id == "zz-first"));
        Assert.Equal("utilities", tools.Single(t => t.Id == "zz-user").Category);
        Assert.Equal("Git", tools.Single(t => t.Id == "git").Name);
    }

    [Fact]
    public void A_tool_without_an_install_has_no_package_or_program() =>
        Assert.Equal(new ToolInstall(null, null, null), ToolCatalog.Parse("""[{"id":"x","name":"x","description":"d"}]""", "mine.json").Single().Install);

    [Fact]
    public async Task An_unreadable_config_lists_no_custom_catalogs()
    {
        await File.WriteAllTextAsync(_home.File(".config/devenv/config.json"), "{not json", Token);

        Assert.Equal(ToolCatalog.Parse(ToolCatalog.BundledText, "bundled").DistinctBy(t => t.Id).Count(), ToolCatalog.Load(_home.Path, _home.Path).Count);
    }

    [Theory]
    [InlineData("{not json", "is not JSON")]
    [InlineData("42", "a list, or an object with a tools list")]
    [InlineData("[1]", "an object per tool")]
    [InlineData("""[{"id":"x","name":"x","description":"d","install":3}]""", "an install object")]
    [InlineData("""[{"id":"x","description":"d","install":{}}]""", "a name on every tool")]
    [InlineData("""[{"id":7,"name":"x","description":"d","install":{}}]""", "is not a tool catalog")]
    public void A_misshapen_catalog_throws_naming_its_source(string text, string message)
    {
        var thrown = Assert.Throws<InvalidDataException>(() => ToolCatalog.Parse(text, "mine.json"));

        Assert.StartsWith("mine.json ", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(message, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_package_manager_is_winget_on_windows_and_brew_elsewhere_when_on_the_path()
    {
        var proc = new FakeProc();

        Assert.IsType<Winget>(PackageManager.For(windows: true, p => p == "winget" ? "/winget" : null, proc));
        Assert.IsType<Homebrew>(PackageManager.For(windows: false, p => p == "brew" ? "/brew" : null, proc));
        Assert.Null(PackageManager.For(windows: true, p => p == "brew" ? "/brew" : null, proc));
        Assert.Null(PackageManager.For(windows: false, _ => null, proc));
    }

    [Fact]
    public async Task Homebrew_reads_brew_list_once_and_matches_a_taps_last_segment()
    {
        var proc = new FakeProc().Answer(new Ran(0, "Git\nfnm  jq\n", string.Empty), "brew", "list");
        var brew = new Homebrew(proc);

        Assert.True(await brew.IsInstalledAsync("git", Token));
        Assert.True(await brew.IsInstalledAsync("schniz/tap/FNM", Token));
        Assert.False(await brew.IsInstalledAsync("gh", Token));
        Assert.Single(proc.Requests);
        Assert.True(proc.Requests[0] is { Attached: false, Timeout.TotalMinutes: 5 });
        Assert.False(await new Homebrew(new FakeProc()).IsInstalledAsync("git", Token));
    }

    [Fact]
    public async Task Homebrew_installs_and_upgrades_attached_unless_quiet()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "brew", "install").Answer(new Ran(1, string.Empty, "no"), "brew", "upgrade");
        var brew = new Homebrew(proc);

        Assert.True(await brew.InstallAsync("gh", quiet: true, Token));
        Assert.False(await brew.UpgradeAllAsync(quiet: false, Token));
        Assert.Equal([["brew", "install", "gh"], ["brew", "upgrade"]], proc.Requests.Select(r => r.Argv));
        Assert.Equal([false, true], proc.Requests.Select(r => r.Attached));
        Assert.Equal("brew", brew.Name);
        Assert.Equal("gh", brew.IdOf(new ToolInstall("GitHub.cli", "gh", null)));
    }

    [Fact]
    public async Task Winget_lists_by_exact_id_and_installs_with_the_agreement_flags()
    {
        var proc = new FakeProc()
            .Answer(new Ran(0, "Name  Id\nGit   git.git\n", string.Empty), "winget", "list", "--id", "Git.Git")
            .Answer(new Ran(0, "No installed package found", string.Empty), "winget", "list", "--id", "GitHub.cli")
            .Answer(new Ran(0, string.Empty, string.Empty), "winget", "install")
            .Answer(new Ran(0, string.Empty, string.Empty), "winget", "upgrade");
        var winget = new Winget(proc);

        Assert.True(await winget.IsInstalledAsync("Git.Git", Token));
        Assert.False(await winget.IsInstalledAsync("GitHub.cli", Token));
        Assert.False(await winget.IsInstalledAsync("Missing.Id", Token));
        Assert.True(await winget.InstallAsync("GitHub.cli", quiet: true, Token));
        Assert.True(await winget.UpgradeAllAsync(quiet: false, Token));

        Assert.Equal(["winget", "list", "--id", "Git.Git", "--exact"], proc.Requests[0].Argv);
        Assert.Equal(["winget", "install", "--id", "GitHub.cli", "--exact", "--accept-package-agreements", "--accept-source-agreements", "--silent", "--disable-interactivity"], proc.Requests[3].Argv);
        Assert.Equal(["winget", "upgrade", "--all", "--accept-package-agreements", "--accept-source-agreements"], proc.Requests[4].Argv);
        Assert.Equal("winget", winget.Name);
        Assert.Equal("GitHub.cli", winget.IdOf(new ToolInstall("GitHub.cli", "gh", null)));
    }

    [Fact]
    public async Task A_tool_is_installed_when_its_program_is_on_the_path_or_its_package_is()
    {
        var proc = new FakeProc().Answer(new Ran(0, "jq\n", string.Empty), "brew", "list").Answer(new Ran(0, string.Empty, string.Empty), "brew", "install");
        var tools = new DevTools(new Homebrew(proc), p => p == "rg" ? "/bin/rg" : null);
        static DevTool Tool(string? brew, string? check) => new("t", "T", "d", "c", new ToolInstall(null, brew, check), false);

        Assert.True(await tools.IsInstalledAsync(Tool("ripgrep", "rg"), Token));
        Assert.True(await tools.IsInstalledAsync(Tool("jq", "nojq"), Token));
        Assert.False(await tools.IsInstalledAsync(Tool("fzf", "fzf"), Token));
        Assert.False(await tools.IsInstalledAsync(Tool(null, "fzf"), Token));
        Assert.True(await tools.InstallAsync(Tool("fzf", "fzf"), quiet: true, Token));
        Assert.False(await tools.InstallAsync(Tool(null, "fzf"), quiet: true, Token));

        var bare = new DevTools(null, _ => null);
        Assert.Null(bare.Manager);
        Assert.False(await bare.IsInstalledAsync(Tool("jq", "jq"), Token));
        Assert.False(await bare.InstallAsync(Tool("jq", "jq"), quiet: true, Token));
    }

    [Theory]
    [InlineData(null, null, "3.13")]
    [InlineData("3.12.4\n", "requires-python = \">=3.11\"", "3.12.4")]
    [InlineData("  \n", "requires-python = \">=3.11\"", "3.11")]
    [InlineData(null, "[project]\nrequires-python='~=3.14.0'", "3.14")]
    [InlineData(null, "requires-python = \">=3\"", "3.13")]
    [InlineData(null, "name = \"x\"", "3.13")]
    public async Task The_python_comes_from_python_version_then_pyproject(string? pinned, string? pyproject, string expected)
    {
        if (pinned is not null)
        {
            await File.WriteAllTextAsync(_home.File(".python-version"), pinned, Token);
        }

        if (pyproject is not null)
        {
            await File.WriteAllTextAsync(_home.File("pyproject.toml"), pyproject, Token);
        }

        Assert.Equal(expected, Runtimes.PythonFor(_home.Path));
    }
}
