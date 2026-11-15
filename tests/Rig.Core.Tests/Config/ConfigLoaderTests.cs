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

    [Fact]
    public void ConfigLoadException_constructors()
    {
        var ex1 = new ConfigLoadException();
        Assert.Equal(ConfigLoadError.InvalidJson, ex1.Kind);
        var ex2 = new ConfigLoadException("msg");
        Assert.Equal("msg", ex2.Message);
        var inner = new InvalidOperationException("inner");
        var ex3 = new ConfigLoadException("msg", inner);
        Assert.Same(inner, ex3.InnerException);
        var ex4 = new ConfigLoadException(ConfigLoadError.Schema, "schema err", "/path", 10, 20, inner);
        Assert.Equal(ConfigLoadError.Schema, ex4.Kind);
        Assert.Equal("/path", ex4.FilePath);
        Assert.Equal(10, ex4.Line);
        Assert.Equal(20, ex4.Column);
    }

    [Fact]
    public void ConfigLoader_validates_null_or_empty_arguments()
    {
        Assert.Throws<ArgumentException>(() => ConfigLoader.Load<ManagedProjectsConfig>("", EstateConfigJsonContext.Default.ManagedProjectsConfig));
        Assert.Throws<ArgumentNullException>(() => ConfigLoader.Load<ManagedProjectsConfig>("/path", null!));
    }

    [Fact]
    public void License_converter_validates_shapes()
    {
        var invalidType = Write("invalid_license.json", """{"defaults": {"license": {"type": 123}}}""");
        Assert.Throws<ConfigLoadException>(() => ConfigLoader.Load(invalidType, EstateConfigJsonContext.Default.RepoPoliciesConfig));

        var invalidShape = Write("invalid_license_shape.json", """{"defaults": {"license": 123}}""");
        Assert.Throws<ConfigLoadException>(() => ConfigLoader.Load(invalidShape, EstateConfigJsonContext.Default.RepoPoliciesConfig));
    }

    [Fact]
    public void Cli_rule_converter_validates_shapes()
    {
        var missingPattern = Write("no_pattern.json", """{"commands": {"test": [{}]}}""");
        Assert.Throws<ConfigLoadException>(() => ConfigLoader.Load(missingPattern, EstateConfigJsonContext.Default.CliAllowlistsConfig));

        var badRationale = Write("bad_rationale.json", """{"commands": {"test": [{"pattern": "x", "rationale": 123}]}}""");
        Assert.Throws<ConfigLoadException>(() => ConfigLoader.Load(badRationale, EstateConfigJsonContext.Default.CliAllowlistsConfig));

        var badExcludes = Write("bad_excludes.json", """{"commands": {"test": [{"pattern": "x", "exclude_systems": "not a list"}]}}""");
        Assert.Throws<ConfigLoadException>(() => ConfigLoader.Load(badExcludes, EstateConfigJsonContext.Default.CliAllowlistsConfig));

        var notObjectOrString = Write("not_obj.json", """{"commands": {"test": [123]}}""");
        Assert.Throws<ConfigLoadException>(() => ConfigLoader.Load(notObjectOrString, EstateConfigJsonContext.Default.CliAllowlistsConfig));
    }
}
