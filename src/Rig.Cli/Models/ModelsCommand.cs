using System.CommandLine;
using System.Text.Json;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core.Text;

namespace Rig.Cli.Models;

/// <summary>
/// <c>rig models</c> (and <c>rig agent models</c>): lists the AI models rig's coding agents run, as a table or JSON.
/// Always exits 0.
/// </summary>
/// <param name="discovery">Finds the models.</param>
/// <param name="output">Where the list goes.</param>
[CliCommand(Name = "models", Description = "Enumerate AI models supported across coding agent CLIs in rig.")]
internal sealed class ModelsCommand(ModelDiscovery discovery, TextWriter output)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal ModelsCommand()
        : this(null!, TextWriter.Null)
    {
    }

    /// <summary>Filter by agent CLI.</summary>
    [CliOption(Name = "--agent", Aliases = ["-a"], Required = false, Description = "Filter by agent CLI: codex, opencode, antigravity (agy), claude-code (claude), or copilot.")]
    public string? Agent { get; set; }

    /// <summary>Filter by upstream model provider.</summary>
    [CliOption(Name = "--provider", Aliases = ["-p"], Required = false, Description = "Filter by upstream model provider: openai, anthropic, google, deepseek, github-copilot, etc.")]
    public string? Provider { get; set; }

    /// <summary>Enumerate every discovered model.</summary>
    [CliOption(Name = "--all", Description = "Enumerate all discovered models across provider CLIs and caches instead of evaluated models only.")]
    public bool All { get; set; }

    /// <summary>Output the list as JSON.</summary>
    [CliOption(Name = "--json", Description = "Output models list as formatted JSON.")]
    public bool Json { get; set; }

    /// <summary>The <c>models</c> command.</summary>
    public Command Build() => DeclarativeCommand.Build(this);

    /// <summary>The handler: the models, filtered.</summary>
    public Task<int> RunAsync(CliContext context) => RunAsync(Agent, Provider, All, Json, context.CancellationToken);

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
            await output.WriteLineAsync(JsonSerializer.Serialize(models, JsonOutputOptions.SnakeCaseIndented)).ConfigureAwait(false);
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
