using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Rig.Cli.Models;

/// <summary>
/// <c>rig models</c> (and <c>rig agent models</c>): lists the AI models rig's coding agents run, as a table or JSON.
/// Ported from <c>rig.models</c>. Always exits 0.
/// </summary>
/// <param name="discovery">Finds the models.</param>
/// <param name="output">Where the list goes.</param>
internal sealed class ModelsCommand(ModelDiscovery discovery, TextWriter output)
{
    private static readonly JsonSerializerOptions Printed = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] Headers = ["Agent / CLI", "Model ID", "Display Name", "Provider", "Effort / Levels"];

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
        var title = all
            ? $"All Discovered AI Coding Models ({models.Count} models)"
            : $"AI Coding Models Evaluated in Rig ({models.Count} models)";
        string[][] rows =
        [
            Headers,
            .. models.Select(m => new[]
            {
                m.Agent, m.ModelId, m.DisplayName, m.Provider ?? "-", m.ReasoningLevels.Count > 0 ? string.Join(", ", m.ReasoningLevels) : "-",
            }),
        ];
        var widths = Enumerable.Range(0, Headers.Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
        var lines = rows.Select(r => string.Join("  ", r.Select((cell, c) => cell.PadRight(widths[c]))).TrimEnd());
        return $"{title}\n{string.Join('\n', lines)}\n";
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
            await output.WriteLineAsync(JsonSerializer.Serialize(models, Printed)).ConfigureAwait(false);
        }
        else if (models.Count == 0)
        {
            await output.WriteLineAsync("No models found matching the specified filters.").ConfigureAwait(false);
        }
        else
        {
            await output.WriteAsync(Render(models, all)).ConfigureAwait(false);
        }

        return 0;
    }
}
