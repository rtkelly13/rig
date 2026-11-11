using System.CommandLine;
using System.Text.Json;
using Rig.Cli.Models;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Models;

/// <summary>Ported from the Python <c>tests/test_models_discovery.py</c>.</summary>
public sealed class ModelsCommandTests : IDisposable
{
    private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("rig-models-");
    private readonly FakeProc _proc = new();
    private readonly StringWriter _out = new();
    private readonly HashSet<string> _onPath = new(StringComparer.Ordinal);

    public void Dispose()
    {
        _out.Dispose();
        _home.Delete(recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ModelDiscovery Discovery => new(_proc, _home.FullName, p => _onPath.Contains(p) ? $"/bin/{p}" : null);

    private void Cache(string relative, string json)
    {
        var path = Path.Join(_home.FullName, relative);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private Task<int> Run(params string[] args) =>
        new ModelsCommand(Discovery, _out).Build().Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = _out, Error = TextWriter.Null }, Ct);

    private JsonElement[] Json() => [.. JsonDocument.Parse(_out.ToString()).RootElement.EnumerateArray()];

    [Fact]
    public void Claude_code_lists_opus()
    {
        var model = Assert.Single(ModelDiscovery.Claude());

        Assert.Equal(("opus", "claude-code", "anthropic"), (model.ModelId, model.Agent, model.Provider));
    }

    [Fact]
    public void Without_caches_codex_and_copilot_list_nothing()
    {
        Assert.Empty(Discovery.Codex());
        Assert.Empty(Discovery.Copilot());
    }

    [Fact]
    public void Codex_reads_its_cache_with_reasoning_levels()
    {
        Cache(".codex/models_cache.json", """
            {"models": [{"slug": "gpt-6.1-sol", "display_name": "GPT-6.1-Sol", "description": "Latest workhorse model",
              "supported_reasoning_levels": [{"effort": "low"}, {"effort": "medium"}, {"effort": "high"}]}]}
            """);

        var model = Assert.Single(Discovery.Codex());

        Assert.Equal(("gpt-6.1-sol", "GPT-6.1-Sol", "openai", "Latest workhorse model"), (model.ModelId, model.DisplayName, model.Provider, model.Description));
        Assert.Equal(["low", "medium", "high"], model.ReasoningLevels);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"models": [{"slug": "x"}]}""")]
    [InlineData("[]")]
    public void A_malformed_codex_cache_lists_nothing(string json)
    {
        Cache(".codex/models_cache.json", json);

        Assert.Empty(Discovery.Codex());
    }

    [Fact]
    public async Task Opencode_lists_what_its_cli_reports()
    {
        _ = _onPath.Add("opencode");
        _ = _proc.Answer(new Ran(0, "\nFetching models\nopencode/claude-opus-5-5\ngithub-copilot/gpt-6.1-sol\nbig-pickle\n", string.Empty), "/bin/opencode", "models");

        var models = await Discovery.OpencodeAsync(Ct);

        Assert.Equal(
            [("opencode/claude-opus-5-5", "claude-opus-5-5", "opencode"), ("github-copilot/gpt-6.1-sol", "gpt-6.1-sol", "github-copilot"), ("big-pickle", "big-pickle", "opencode")],
            models.Select(m => (m.ModelId, m.DisplayName, m.Provider)));
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(_proc.Requests).Timeout);
    }

    [Fact]
    public async Task Opencode2_stands_in_for_opencode()
    {
        _ = _onPath.Add("opencode2");
        _ = _proc.Answer(new Ran(0, "a/b\n", string.Empty), "/bin/opencode2", "models");

        Assert.Equal("a/b", Assert.Single(await Discovery.OpencodeAsync(Ct)).ModelId);
    }

    [Fact]
    public async Task When_the_opencode_cli_fails_its_cache_is_read_for_priority_providers()
    {
        _ = _onPath.Add("opencode");
        _ = _proc.Answer(new Ran(Ran.TimedOut, string.Empty, "timed out"), "/bin/opencode");
        Cache(".cache/opencode/models.json", """
            {"deepseek": {"models": {"deepseek-flash": {"name": "DeepSeek V4.1 Flash", "description": "Fast multimodal model"}}},
             "someone-else": {"models": {"m": {"name": "M"}}},
             "openai": {"models": {"gpt-x": {"name": ""}}}}
            """);

        var models = await Discovery.OpencodeAsync(Ct);

        Assert.Equal(
            [("deepseek/deepseek-flash", "DeepSeek V4.1 Flash", "deepseek", "Fast multimodal model"), ("openai/gpt-x", "gpt-x", "openai", null)],
            models.Select(m => (m.ModelId, m.DisplayName, m.Provider, m.Description)));
    }

    [Fact]
    public async Task Copilot_reads_the_github_copilot_provider_from_the_opencode_cache()
    {
        Cache(".cache/opencode/models.json", """{"github-copilot": {"models": {"gpt-6.1-sol": {"name": "Sol"}}}, "openai": {"models": {}}}""");

        Assert.Equal([("gpt-6.1-sol", "Sol", "github-copilot")], Discovery.Copilot().Select(m => (m.ModelId, m.DisplayName, m.Provider)));
        Assert.Equal(["github-copilot/gpt-6.1-sol"], (await Discovery.OpencodeAsync(Ct)).Select(m => m.ModelId));
    }

    [Fact]
    public async Task Antigravity_lists_agy_models_split_on_wide_gaps()
    {
        Assert.Empty(await Discovery.AntigravityAsync(Ct));

        _ = _onPath.Add("agy");
        _ = _proc.Answer(new Ran(0, "Fetching...\ngemini-3.8-flash   Gemini 3.8 Flash\nclaude-opus   Claude  Opus\ngpt-6   GPT 6\nlonely\n", string.Empty), "agy", "models");

        var models = await Discovery.AntigravityAsync(Ct);

        Assert.Equal(
            [("gemini-3.8-flash", "Gemini 3.8 Flash", "google"), ("claude-opus", "Claude  Opus", "anthropic"), ("gpt-6", "GPT 6", "openai")],
            models.Select(m => (m.ModelId, m.DisplayName, m.Provider)));
    }

    [Fact]
    public async Task By_default_only_the_evaluated_models_are_listed()
    {
        var models = await Discovery.DiscoverAsync(null, all: false, Ct);

        Assert.Equal(["claude-code", "codex", "antigravity", "opencode"], models.Select(m => m.Agent));
        Assert.Equal(["opus", "gpt-6.1-sol", "gemini-3.8-flash-medium", "deepseek/deepseek-flash"], models.Select(m => m.ModelId));
        Assert.Empty(_proc.Requests);
    }

    [Theory]
    [InlineData("claude", "claude-code")]
    [InlineData(" CCC ", "claude-code")]
    [InlineData("openai", "codex")]
    [InlineData("gemini", "antigravity")]
    [InlineData("oc", "opencode")]
    [InlineData("cpl", "github-copilot")]
    public async Task An_agent_alias_filters_the_evaluated_models(string alias, string agent)
    {
        var model = Assert.Single(await Discovery.DiscoverAsync(alias, all: false, Ct));

        Assert.Equal(agent, model.Agent);
    }

    [Fact]
    public async Task An_unknown_agent_lists_nothing()
    {
        Assert.Empty(await Discovery.DiscoverAsync("vim", all: false, Ct));
        Assert.Empty(await Discovery.DiscoverAsync("vim", all: true, Ct));
    }

    [Fact]
    public async Task All_asks_every_source_in_order()
    {
        _ = _onPath.Add("agy");
        _ = _onPath.Add("opencode");
        _ = _proc.Answer(new Ran(0, "g-1  G1\n", string.Empty), "agy")
            .Answer(new Ran(0, "x/y\n", string.Empty), "/bin/opencode");
        Cache(".codex/models_cache.json", """{"models": [{"slug": "c-1", "display_name": "C1"}]}""");
        Cache(".cache/opencode/models.json", """{"github-copilot": {"models": {"p-1": {}}}}""");

        var models = await Discovery.DiscoverAsync(null, all: true, Ct);

        Assert.Equal(["opus", "c-1", "g-1", "p-1", "x/y"], models.Select(m => m.ModelId));
        Assert.Equal(["c-1"], (await Discovery.DiscoverAsync("codex", all: true, Ct)).Select(m => m.ModelId));
        Assert.Equal(["opus"], (await Discovery.DiscoverAsync("claude", all: true, Ct)).Select(m => m.ModelId));
        Assert.Equal(["g-1"], (await Discovery.DiscoverAsync("agy", all: true, Ct)).Select(m => m.ModelId));
        Assert.Equal(["p-1"], (await Discovery.DiscoverAsync("copilot", all: true, Ct)).Select(m => m.ModelId));
        Assert.Equal(["x/y"], (await Discovery.DiscoverAsync("opencode", all: true, Ct)).Select(m => m.ModelId));
    }

    [Fact]
    public async Task Json_prints_every_field_snake_cased()
    {
        Assert.Equal(0, await Run("models", "--agent", "claude", "--json"));

        Assert.Equal(
            """
            [
              {
                "agent": "claude-code",
                "model_id": "opus",
                "display_name": "Claude Opus 5.5 (Medium)",
                "provider": "anthropic",
                "reasoning_levels": [
                  "medium"
                ],
                "description": "Anthropic Claude Opus 5.5 evaluated at medium reasoning effort."
              }
            ]

            """.ReplaceLineEndings("\n"),
            _out.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_provider_filter_matches_part_of_the_provider()
    {
        Assert.Equal(0, await Run("models", "-p", "ANTHRO", "--json"));

        Assert.All(Json(), m => Assert.Equal("anthropic", m.GetProperty("provider").GetString()));
        Assert.Single(Json());
    }

    [Fact]
    public async Task The_table_is_titled_and_aligned()
    {
        Assert.Equal(0, await Run("models", "-a", "codex"));

        Assert.Equal(
            """
            AI Coding Models Evaluated in Rig (1 models)
            Agent / CLI  Model ID     Display Name          Provider  Effort / Levels
            codex        gpt-6.1-sol  GPT-6.1 Sol (Medium)  openai    medium

            """.ReplaceLineEndings("\n"),
            _out.ToString());
    }

    [Fact]
    public void Missing_provider_and_levels_show_a_dash_and_all_changes_the_title()
    {
        var table = ModelsCommand.Render([new DiscoveredModel("opencode", "x", "X", null, [])], all: true);

        Assert.StartsWith("All Discovered AI Coding Models (1 models)\n", table, StringComparison.Ordinal);
        Assert.EndsWith("opencode     x         X             -         -\n", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_found_says_so()
    {
        Assert.Equal(0, await Run("models", "--provider", "nobody"));

        Assert.Equal("No models found matching the specified filters.\n", _out.ToString().ReplaceLineEndings("\n"));
    }
}
