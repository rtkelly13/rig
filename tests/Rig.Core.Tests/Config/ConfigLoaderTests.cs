using System.Text.Json;
using Rig.Core.Config;

namespace Rig.Core.Tests.Config;

/// <summary><see cref="ConfigLoader"/> and the source-generated <see cref="EstateConfigJsonContext"/> over temp files.</summary>
public sealed class ConfigLoaderTests : IDisposable
{
    private const string Projects = """
        {"projects": [{"name": "rig", "repo": "git@github.com:o/rig.git", "orca": {"group": "tools"}}]}
        """;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string name, string json)
    {
        var path = _dir.File(name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_typed_document_round_trips_byte_for_byte()
    {
        var file = Write("managed-projects.json", Projects);

        var loaded = ConfigLoader.Load(file, EstateConfigJsonContext.Default.ManagedProjectsConfig);
        var once = JsonSerializer.Serialize(loaded, EstateConfigJsonContext.Default.ManagedProjectsConfig);
        var reloaded = ConfigLoader.Parse(once, file, EstateConfigJsonContext.Default.ManagedProjectsConfig);

        Assert.Equal(once, JsonSerializer.Serialize(reloaded, EstateConfigJsonContext.Default.ManagedProjectsConfig));
        Assert.Equal("rig", Assert.Single(loaded.Projects!).Name);
        Assert.Equal("tools", loaded.Projects![0].Orca!.Group);
    }

    [Fact]
    public void A_schema_error_names_the_source_line_column_and_path()
    {
        var file = Write("managed-projects.json", "{\n  \"projects\": \"nope\"\n}");

        Assert.False(ConfigLoader.TryLoad(file, EstateConfigJsonContext.Default.ManagedProjectsConfig, out var result, out var errors));

        Assert.Null(result);
        var error = Assert.Single(errors);
        Assert.StartsWith($"{file}:2:", error, StringComparison.Ordinal);
        Assert.Contains("$.projects", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_JSON_is_reported_as_such()
    {
        var file = Write("estate-scopes.json", "{not json");

        var error = Assert.Throws<ConfigLoadException>(() => ConfigLoader.Load(file, EstateConfigJsonContext.Default.EstateScopesConfig));

        Assert.Equal(ConfigLoadError.InvalidJson, error.Kind);
        Assert.StartsWith($"{file}:1:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_is_a_load_error()
    {
        var path = Path.Join(_dir.Path, "absent.json");

        var error = Assert.Throws<ConfigLoadException>(() => ConfigLoader.Load(path, EstateConfigJsonContext.Default.EstateScopesConfig));

        Assert.Equal(ConfigLoadError.MissingFile, error.Kind);
        Assert.Equal(path, error.FilePath);
    }

    [Fact]
    public void The_checked_in_configurations_all_load()
    {
        var config = Path.Join(Path.GetDirectoryName(RepoTemp.Root)!, "config");

        Assert.NotEmpty(ConfigLoader.Load(Path.Join(config, "managed-projects.json"), EstateConfigJsonContext.Default.ManagedProjectsConfig).Projects!);
        Assert.NotNull(ConfigLoader.Load(Path.Join(config, "repo_policies.json"), EstateConfigJsonContext.Default.RepoPoliciesConfig).Defaults);
        Assert.NotNull(ConfigLoader.Load(Path.Join(config, "cli-allowlists.json"), EstateConfigJsonContext.Default.CliAllowlistsConfig).Commands);
        Assert.NotEmpty(ConfigLoader.Load(Path.Join(config, "estate-scopes.json"), EstateConfigJsonContext.Default.EstateScopesConfig).Personal!.Orgs!);
    }

    [Fact]
    public void The_policy_configuration_round_trips_byte_for_byte()
    {
        var file = Path.Join(Path.GetDirectoryName(RepoTemp.Root)!, "config", "repo_policies.json");

        var once = JsonSerializer.Serialize(ConfigLoader.Load(file, EstateConfigJsonContext.Default.RepoPoliciesConfig), EstateConfigJsonContext.Default.RepoPoliciesConfig);
        var again = JsonSerializer.Serialize(ConfigLoader.Parse(once, file, EstateConfigJsonContext.Default.RepoPoliciesConfig), EstateConfigJsonContext.Default.RepoPoliciesConfig);

        Assert.Equal(once, again);
    }

    [Fact]
    public void The_command_policy_round_trips_bare_and_annotated_rules()
    {
        var file = Path.Join(Path.GetDirectoryName(RepoTemp.Root)!, "config", "cli-allowlists.json");

        var once = JsonSerializer.Serialize(ConfigLoader.Load(file, EstateConfigJsonContext.Default.CliAllowlistsConfig), EstateConfigJsonContext.Default.CliAllowlistsConfig);
        var again = JsonSerializer.Serialize(ConfigLoader.Parse(once, file, EstateConfigJsonContext.Default.CliAllowlistsConfig), EstateConfigJsonContext.Default.CliAllowlistsConfig);

        Assert.Equal(once, again);
        Assert.Contains("\"pattern\":", once, StringComparison.Ordinal);
    }
}
