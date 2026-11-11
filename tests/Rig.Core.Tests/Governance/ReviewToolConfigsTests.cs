using System.Text.Json.Nodes;
using Rig.Core.Governance;

namespace Rig.Core.Tests.Governance;

/// <summary><see cref="ReviewToolConfigs"/> over temp repos. Ported from the review tool config tests.</summary>
public sealed class ReviewToolConfigsTests : IDisposable
{
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static RepoPolicies Disallowing(params string[] slugs)
    {
        var apps = new JsonObject();
        foreach (var slug in slugs)
        {
            apps[slug] = new JsonObject { ["accounts"] = new JsonObject { ["o"] = new JsonObject { ["disallow_repos"] = new JsonArray("app") } } };
        }

        return RepoPolicies.Parse(new JsonObject { ["github_apps"] = apps }, "policy.json");
    }

    private string Repo => _root.Folder("app");

    [Fact]
    public void A_bare_repo_misses_each_config_and_qodana_is_optional()
    {
        var statuses = ReviewToolConfigs.Inspect(Repo);

        Assert.Equal(["coderabbit", "codecov", "deepsource", "qodana"], statuses.Select(s => s.Tool));
        Assert.Equal(["MISSING", "MISSING", "MISSING", "OPTIONAL"], statuses.Select(s => s.Status.Status));
        Assert.True(statuses[3].Status.IsAligned);
    }

    [Fact]
    public void A_dotnet_repo_needs_qodana_and_its_workflow()
    {
        _root.File("app/src/Lib/Lib.csproj");
        _root.File("app/qodana.yaml");

        Assert.True(ReviewToolConfigs.IsDotnetRepo(Repo));
        Assert.Equal(new ConfigStatus("CONFIGURED", false, Path.Join(Repo, "qodana.yaml")), ReviewToolConfigs.InspectQodana(Repo));
        Assert.Equal((true, "created .github/workflows/qodana.yml"), ReviewToolConfigs.AlignQodana(Repo, dryRun: false));
        Assert.True(ReviewToolConfigs.InspectQodana(Repo).IsAligned);
    }

    [Fact]
    public void Markers_under_build_and_hidden_folders_do_not_make_a_dotnet_repo()
    {
        _root.File("app/bin/x.csproj");
        _root.File("app/.cache/y.sln");

        Assert.False(ReviewToolConfigs.IsDotnetRepo(Repo));
        Assert.Equal((false, "not a .NET repository (skipped)"), ReviewToolConfigs.AlignQodana(Repo, dryRun: false));
    }

    [Fact]
    public void Deepsource_enables_the_languages_at_the_root_and_one_level_down()
    {
        _root.File("app/pyproject.toml");
        _root.File("app/web/index.ts");
        _root.File("app/node_modules/x.js");

        var config = ReviewToolConfigs.DeepSourceConfig(Repo);

        Assert.Contains("name = \"python\"", config, StringComparison.Ordinal);
        Assert.Contains("name = \"javascript\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Align_creates_the_missing_configs_and_a_dry_run_writes_nothing()
    {
        var dry = ReviewToolConfigs.Align(Repo, "o", RepoPolicies.Empty, dryRun: true);
        Assert.All(dry.Take(3), change => Assert.True(change.Changed));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Repo));

        _ = ReviewToolConfigs.Align(Repo, "o", RepoPolicies.Empty, dryRun: false);

        Assert.All(ReviewToolConfigs.Inspect(Repo), status => Assert.True(status.Status.IsAligned));
        Assert.All(ReviewToolConfigs.Align(Repo, "o", RepoPolicies.Empty, dryRun: false), change => Assert.False(change.Changed));
    }

    [Fact]
    public void A_disallowed_coderabbit_gets_a_suppressor_and_other_disallowed_tools_are_skipped()
    {
        var changes = ReviewToolConfigs.Align(Repo, "o", Disallowing("coderabbitai", "codecov"), dryRun: false);

        Assert.Equal(new ToolChange("CodeRabbit (.coderabbit.yaml)", true, "disallowed by policy: configured suppressor (auto_review: false)"), changes[0]);
        Assert.Equal(new ToolChange("Codecov (codecov.yml)", false, "disallowed by policy: skipped"), changes[1]);
        Assert.False(File.Exists(Path.Join(Repo, "codecov.yml")));
        Assert.Equal(
            "disallowed by policy: suppressor already present",
            ReviewToolConfigs.Align(Repo, "o", Disallowing("coderabbitai"), dryRun: false)[0].Message);
    }

    [Fact]
    public void A_disallowed_coderabbit_with_a_reviewing_config_is_a_conflict_left_unchanged()
    {
        var file = _root.File("app/.coderabbit.yml");
        File.WriteAllText(file, "reviews:\n  auto_review:\n    enabled: true\n");

        var change = ReviewToolConfigs.Align(Repo, "o", Disallowing("coderabbitai"), dryRun: false)[0];

        Assert.StartsWith("CONFLICT: disallowed by policy but .coderabbit.yml", change.Message, StringComparison.Ordinal);
        Assert.Equal("reviews:\n  auto_review:\n    enabled: true\n", File.ReadAllText(file));
    }

    [Fact]
    public void A_dotnet_repo_without_qodana_gets_its_config_and_workflow()
    {
        _root.File("app/App.sln");

        var dry = ReviewToolConfigs.AlignQodana(Repo, dryRun: true);
        var wrote = ReviewToolConfigs.AlignQodana(Repo, dryRun: false);

        Assert.Equal((true, "would create qodana.yaml, .github/workflows/qodana.yml"), dry);
        Assert.Equal((true, "created qodana.yaml, .github/workflows/qodana.yml"), wrote);
        Assert.Equal((false, "already configured"), ReviewToolConfigs.AlignQodana(Repo, dryRun: false));
        Assert.True(ReviewToolConfigs.InspectQodana(Repo).IsAligned);
    }

    [Fact]
    public void Deepsource_enables_csharp_and_finds_python_or_javascript_one_level_down()
    {
        _root.File("app/App.csproj");
        _root.File("app/package.json");
        _root.File("app/tools/run.py");

        var config = ReviewToolConfigs.DeepSourceConfig(Repo);

        Assert.Contains("name = \"csharp\"", config, StringComparison.Ordinal);
        Assert.Contains("name = \"python\"", config, StringComparison.Ordinal);
        Assert.Contains("name = \"javascript\"", config, StringComparison.Ordinal);
        Assert.Contains("name = \"secrets\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparseable_coderabbit_config_does_not_count_as_a_suppressor()
    {
        var file = _root.File("app/.coderabbit.yaml");
        File.WriteAllText(file, "reviews: [unclosed\n");

        var change = ReviewToolConfigs.Align(Repo, "o", Disallowing("coderabbitai"), dryRun: false)[0];

        Assert.StartsWith("CONFLICT: disallowed by policy but .coderabbit.yaml", change.Message, StringComparison.Ordinal);
    }
}
