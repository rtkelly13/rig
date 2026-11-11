using System.Text.Json.Nodes;
using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>Obsidian plugin resolution, enablement and install; ported from <c>test_obsidian_plugins.py</c>.</summary>
public sealed class ObsidianPluginsTests : IDisposable
{
    private readonly TempDir _vault = new();
    private readonly PluginServer _server = new();

    public void Dispose()
    {
        _server.Dispose();
        _vault.Dispose();
    }

    private string Config(string name) => Path.Join(_vault.Path, ".obsidian", name);

    private string Written(string name) => File.ReadAllText(Config(name)).ReplaceLineEndings("\n");

    [Fact]
    public void The_registry_keeps_well_formed_entries_and_skips_the_rest()
    {
        var registry = ObsidianPlugins.ParseRegistry(JsonNode.Parse("""
            [{"id": "no-repo", "name": "No Repo"}, "not-an-object",
             {"id": "dataview", "repo": "blacksmithgu/obsidian-dataview", "name": "Dataview", "author": "x"}]
            """));

        Assert.Equal([new PluginSpec("dataview", "blacksmithgu/obsidian-dataview", "Dataview")], registry);
    }

    [Fact]
    public void A_registry_that_is_not_a_list_is_an_error() =>
        _ = Assert.Throws<InvalidDataException>(() => ObsidianPlugins.ParseRegistry(JsonNode.Parse("""{"plugins": []}""")));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://github.com/x/y")]
    [InlineData("https://evil.example.com/main.js")]
    [InlineData("not a url")]
    public void Plugin_urls_must_be_https_from_a_known_host(string address) =>
        _ = Assert.Throws<InvalidDataException>(() => ObsidianPlugins.CheckUrl(address));

    [Fact]
    public void A_known_https_url_passes() =>
        Assert.Equal("raw.githubusercontent.com", ObsidianPlugins.CheckUrl(ObsidianPlugins.RegistryUrl).Host);

    [Fact]
    public async Task The_installed_version_comes_from_the_manifest()
    {
        await File.WriteAllTextAsync(_vault.File(".obsidian/plugins/dataview/manifest.json"), """{"version": "0.5.67"}""", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(_vault.File(".obsidian/plugins/bare/manifest.json"), """{"id": "bare"}""", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(_vault.File(".obsidian/plugins/broken/manifest.json"), "{not json", TestContext.Current.CancellationToken);

        Assert.Equal("0.5.67", ObsidianPlugins.InstalledVersion(_vault.Path, "dataview"));
        Assert.Null(ObsidianPlugins.InstalledVersion(_vault.Path, "bare"));
        Assert.Null(ObsidianPlugins.InstalledVersion(_vault.Path, "broken"));
        Assert.Null(ObsidianPlugins.InstalledVersion(_vault.Path, "absent"));
    }

    [Fact]
    public async Task Enabling_a_plugin_keeps_the_ones_already_enabled_and_is_idempotent()
    {
        await File.WriteAllTextAsync(_vault.File(".obsidian/community-plugins.json"), """["dataview", "templater-obsidian"]""", TestContext.Current.CancellationToken);

        Assert.True(ObsidianPlugins.Enable(_vault.Path, "obsidian-local-rest-api", dryRun: false));
        Assert.False(ObsidianPlugins.Enable(_vault.Path, "dataview", dryRun: false));
        Assert.Equal("[\n  \"dataview\",\n  \"templater-obsidian\",\n  \"obsidian-local-rest-api\"\n]\n", Written("community-plugins.json"));
    }

    [Fact]
    public async Task Enabling_survives_a_corrupt_config()
    {
        await File.WriteAllTextAsync(_vault.File(".obsidian/community-plugins.json"), "{not json", TestContext.Current.CancellationToken);

        Assert.True(ObsidianPlugins.Enable(_vault.Path, "dataview", dryRun: false));
        Assert.Equal("[\n  \"dataview\"\n]\n", Written("community-plugins.json"));
    }

    [Fact]
    public void A_dry_run_enable_writes_nothing()
    {
        Assert.True(ObsidianPlugins.Enable(_vault.Path, "dataview", dryRun: true));
        Assert.False(File.Exists(Config("community-plugins.json")));
    }

    [Fact]
    public async Task Core_plugins_flip_in_the_mapping_shape()
    {
        await File.WriteAllTextAsync(_vault.File(".obsidian/core-plugins.json"), """{"properties": false, "graph": true}""", TestContext.Current.CancellationToken);

        Assert.Equal(["properties"], ObsidianPlugins.EnableCore(_vault.Path, ["properties"], dryRun: false));
        Assert.Equal("{\n  \"properties\": true,\n  \"graph\": true\n}\n", Written("core-plugins.json"));
        Assert.Empty(ObsidianPlugins.EnableCore(_vault.Path, ["properties"], dryRun: false));
    }

    [Fact]
    public async Task Core_plugins_keep_the_legacy_list_shape()
    {
        await File.WriteAllTextAsync(_vault.File(".obsidian/core-plugins.json"), """["graph", "backlink"]""", TestContext.Current.CancellationToken);

        Assert.Equal(["properties"], ObsidianPlugins.EnableCore(_vault.Path, ["properties"], dryRun: false));
        Assert.Equal("[\n  \"graph\",\n  \"backlink\",\n  \"properties\"\n]\n", Written("core-plugins.json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("3")]
    public async Task Core_plugins_need_a_readable_file(string? content)
    {
        _ = _vault.Folder(".obsidian");
        if (content is not null)
        {
            await File.WriteAllTextAsync(Config("core-plugins.json"), content, TestContext.Current.CancellationToken);
        }

        Assert.Empty(ObsidianPlugins.EnableCore(_vault.Path, ["properties"], dryRun: false));
    }

    [Fact]
    public async Task Install_downloads_the_release_assets()
    {
        _ = _server.WithPlugins("1.2.0", "dataview");

        var install = await _server.Plugins.InstallAsync(_vault.Path, new PluginSpec("dataview", "owner/dataview", "Dataview"), dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(new PluginInstall("1.2.0", null), install);
        Assert.Equal("1.2.0", ObsidianPlugins.InstalledVersion(_vault.Path, "dataview"));
        Assert.Equal("// dataview styles.css", await File.ReadAllTextAsync(Config("plugins/dataview/styles.css"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_dry_run_install_only_reads_the_release()
    {
        _ = _server.WithPlugins("1.2.0", "dataview");

        var install = await _server.Plugins.InstallAsync(_vault.Path, new PluginSpec("dataview", "owner/dataview", "Dataview"), dryRun: true, TestContext.Current.CancellationToken);

        Assert.Equal("1.2.0", install.Tag);
        Assert.Equal([PluginServer.Release("dataview")], _server.Asked);
        Assert.False(Directory.Exists(Config("plugins")));
    }

    [Theory]
    [InlineData("""{"assets": []}""")]
    [InlineData("""{"tag_name": "1.0.0"}""")]
    [InlineData("""{"tag_name": "1.0.0", "assets": [{"name": "main.js"}]}""")]
    [InlineData("[]")]
    [InlineData("{not json")]
    public async Task A_misshapen_release_is_reported_and_nothing_is_installed(string release)
    {
        _server.Bodies[PluginServer.Release("dataview")] = release;

        var install = await _server.Plugins.InstallAsync(_vault.Path, new PluginSpec("dataview", "owner/dataview", "Dataview"), dryRun: false, TestContext.Current.CancellationToken);

        Assert.Null(install.Tag);
        Assert.StartsWith("dataview: could not read latest release (", install.Problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Config("plugins")));
    }

    [Fact]
    public async Task A_release_without_main_js_is_skipped()
    {
        _server.Bodies[PluginServer.Release("dataview")] = """{"tag_name": "2.0", "assets": [{"name": "manifest.json", "browser_download_url": "https://github.com/m"}]}""";

        var install = await _server.Plugins.InstallAsync(_vault.Path, new PluginSpec("dataview", "owner/dataview", "Dataview"), dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(new PluginInstall(null, "dataview: release 2.0 is missing main.js"), install);
    }

    [Fact]
    public async Task An_asset_from_an_unknown_host_is_refused()
    {
        _ = _server.WithPlugins("1.0", "dataview");
        _server.Bodies[PluginServer.Release("dataview")] = """
            {"tag_name": "1.0", "assets": [{"name": "manifest.json", "browser_download_url": "https://evil.example.com/m"},
             {"name": "main.js", "browser_download_url": "https://github.com/owner/dataview/releases/download/1.0/main.js"}]}
            """;

        var install = await _server.Plugins.InstallAsync(_vault.Path, new PluginSpec("dataview", "owner/dataview", "Dataview"), dryRun: false, TestContext.Current.CancellationToken);

        Assert.StartsWith("dataview: failed downloading manifest.json (refusing plugin URL from unexpected host", install.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("https://evil.example.com/m", _server.Asked);
    }

    [Fact]
    public void The_default_vault_is_in_the_estate() =>
        Assert.Equal(Path.Join("/h", "code", "personal", "obsidian-vault"), ObsidianPlugins.DefaultVault("/h"));
}
