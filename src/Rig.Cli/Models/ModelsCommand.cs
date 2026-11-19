#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Text.Json;
using Rig.Cli.Rendering;
using Rig.Core.Text;

namespace Rig.Cli.Models;

/// <summary>
/// <c>rig models</c> (and <c>rig agent models</c>): lists the AI models rig's coding agents run, as a table or JSON.
/// Always exits 0.
/// </summary>
/// <param name="discovery">Finds the models.</param>
/// <param name="output">Where the list goes.</param>
internal sealed class ModelsCommand(ModelDiscovery discovery, TextWriter output)
{
    /// <summary>The <c>models</c> command, named <paramref name="name"/>.</summary>
    public Command Build(string name = "models")
    {
        var agent = new Option<string?>("--agent", "-a") { Description = "Filter by agent CLI: codex, opencode, antigravity (agy), claude-code (claude), or copilot." };
        var provider = new Option<string?>("--provider", "-p") { Description = "Filter by upstream model provider: openai, anthropic, google, deepseek, github-copilot, etc." };
        var all = new Option<bool>("--all") { Description = "Enumerate all discovered models across provider CLIs and caches instead of evaluated models only." };
        var json = new Option<bool>("--json") { Description = "Output models list as formatted JSON." };
        var models = new Command(name, "Enumerate AI models supported across coding agent CLIs in rig.") { agent, provider, all, json };
        models.SetAction((parsed, ct) => RunAsync(parsed.GetValue(agent), parsed.GetValue(provider), parsed.GetValue(all), parsed.GetValue(json), ct));
        return models;
    }

    /// <summary>The models as a titled plain-text table.</summary>
    public static string Render(IReadOnlyList<DiscoveredModel> models, bool all)
    {
        ArgumentNullException.ThrowIfNull(models);
        var table = new TextTable(
            all ? $"All Discovered AI Coding Models ({models.Count} models)" : $"AI Coding Models Evaluated in Rig ({models.Count} models)",
            [("Agent / CLI", false), ("Model ID", false), ("Display Name", false), ("Provider", false), ("Effort / Levels", false)]);
        foreach (var model in models)
        {
            _ = table.Add(
                model.Agent,
                model.ModelId,
                model.DisplayName,
                model.Provider ?? "-",
                model.ReasoningLevels.Count > 0 ? string.Join(", ", model.ReasoningLevels) : "-");
        }

        return table.Render();
    }

    private async Task<int> RunAsync(string? agent, string? provider, bool all, bool json, CancellationToken cancellationToken)
    {
        IReadOnlyList<DiscoveredModel> models = await discovery.DiscoverAsync(agent, all, cancellationToken).ConfigureAwait(false);
        var wanted = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (wanted.Length > 0)
        {
            models = [.. models.Where(m => m.Provider is { } p && p.ToLowerInvariant().Contains(wanted, StringComparison.Ordinal))];
        }

        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(models, JsonOutputOptions.SnakeCaseIndented), cancellationToken).ConfigureAwait(false);
        }
        else if (models.Count == 0)
        {
            await output.WriteLineAsync("No models found matching the specified filters.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await output.WriteAsync(Render(models, all), cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }
}
