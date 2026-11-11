using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Cli.Models;

/// <summary>One AI model a rig coding agent can run.</summary>
/// <param name="Agent">The agent CLI that runs it: claude-code, codex, antigravity, opencode or github-copilot.</param>
/// <param name="ModelId">The id the agent takes.</param>
/// <param name="DisplayName">Its human name.</param>
/// <param name="Provider">Who serves it, when known.</param>
/// <param name="ReasoningLevels">The reasoning effort tiers it offers.</param>
/// <param name="Description">A line about it, when known.</param>
internal sealed record DiscoveredModel(
    string Agent, string ModelId, string DisplayName, string? Provider, IReadOnlyList<string> ReasoningLevels, string? Description = null);

/// <summary>
/// Finds the models each agent CLI offers. By default only the models rig evaluates; with <c>--all</c>, what the CLIs
/// and their local caches report (<c>opencode models</c>, <c>agy models</c>, <c>~/.codex/models_cache.json</c>,
/// <c>~/.cache/opencode/models.json</c>). Anything missing, failing or malformed contributes no models.
/// </summary>
/// <param name="proc">Runs the agent CLIs.</param>
/// <param name="home">The user's home folder, where the caches live.</param>
/// <param name="which">Finds a program on PATH, or null.</param>
internal sealed partial class ModelDiscovery(IProc proc, string home, Func<string, string?> which)
{
    /// <summary>The models rig evaluates across developer environments.</summary>
    public static readonly IReadOnlyList<DiscoveredModel> Evaluated =
    [
        new("claude-code", "opus", "Claude Opus 5.5 (Medium)", "anthropic", ["medium"], "Anthropic Claude Opus 5.5 evaluated at medium reasoning effort."),
        new("codex", "gpt-6.1-sol", "GPT-6.1 Sol (Medium)", "openai", ["medium"], "OpenAI GPT-6.1 Sol evaluated at medium reasoning effort."),
        new("antigravity", "gemini-3.8-flash-medium", "Gemini 3.8 Flash (Medium)", "google", ["medium"], "Google Gemini 3.8 Flash evaluated at medium reasoning effort."),
        new("opencode", "deepseek/deepseek-flash", "DeepSeek Flash 4.1", "deepseek", ["medium"], "DeepSeek Flash 4.1 evaluated model for high-throughput reasoning."),
    ];

    /// <summary>The Copilot model rig evaluates, listed only when Copilot is asked for.</summary>
    public static readonly IReadOnlyList<DiscoveredModel> CopilotEvaluated =
    [
        new("github-copilot", "gpt-6.1-sol", "GPT-6.1 Sol (Medium)", "openai", ["medium"], "OpenAI Sol 6.1 served via GitHub Copilot."),
    ];

    // The OpenCode catalog's providers worth listing for rig.
    private static readonly HashSet<string> PriorityProviders = new(StringComparer.Ordinal)
    {
        "opencode", "github-copilot", "anthropic", "openai", "google", "deepseek",
    };

    private string CodexCache => Path.Join(home, ".codex", "models_cache.json");

    private string OpencodeCache => Path.Join(home, ".cache", "opencode", "models.json");

    /// <summary>The models for <paramref name="agent"/> (any alias; every agent when null), evaluated or all.</summary>
    public async Task<IReadOnlyList<DiscoveredModel>> DiscoverAsync(string? agent, bool all, CancellationToken cancellationToken)
    {
        var key = AgentKey(agent);
        if (!all)
        {
            return key switch
            {
                null => Evaluated,
                "github-copilot" => CopilotEvaluated,
                "" => [],
                _ => [.. Evaluated.Where(m => m.Agent == key)],
            };
        }

        return key switch
        {
            "claude-code" => Claude(),
            "codex" => Codex(),
            "opencode" => await OpencodeAsync(cancellationToken).ConfigureAwait(false),
            "antigravity" => await AntigravityAsync(cancellationToken).ConfigureAwait(false),
            "github-copilot" => Copilot(),
            "" => [],
            _ =>
            [
                .. Claude(),
                .. Codex(),
                .. await AntigravityAsync(cancellationToken).ConfigureAwait(false),
                .. Copilot(),
                .. await OpencodeAsync(cancellationToken).ConfigureAwait(false),
            ],
        };
    }

    /// <summary>Claude Code's evaluated models; it has no catalog to read.</summary>
    public static IReadOnlyList<DiscoveredModel> Claude() => [.. Evaluated.Where(m => m.Agent == "claude-code")];

    /// <summary>The models in Codex's cache.</summary>
    public IReadOnlyList<DiscoveredModel> Codex()
    {
        if (ReadObject(CodexCache) is not { } cache || cache["models"] is not JsonArray models)
        {
            return [];
        }

        List<DiscoveredModel> found = [];
        foreach (var entry in models)
        {
            if (entry is not JsonObject model || Text(model["slug"]) is not { } slug || Text(model["display_name"]) is not { } name)
            {
                return [];
            }

            var levels = (model["supported_reasoning_levels"] as JsonArray ?? [])
                .Select(level => Text((level as JsonObject)?["effort"]))
                .OfType<string>();
            found.Add(new DiscoveredModel("codex", slug, name, "openai", [.. levels], Text(model["description"])));
        }

        return found;
    }

    /// <summary>OpenCode's models: what <c>opencode models</c> lists, else the priority providers in its cache.</summary>
    public async Task<IReadOnlyList<DiscoveredModel>> OpencodeAsync(CancellationToken cancellationToken)
    {
        if ((which("opencode") ?? which("opencode2")) is { } executable)
        {
            var ran = await proc.RunAsync(new ProcRequest([executable, "models"]) { Timeout = TimeSpan.FromSeconds(3) }, cancellationToken).ConfigureAwait(false);
            if (ran.Ok)
            {
                List<DiscoveredModel> listed = [];
                foreach (var line in Lines(ran.Out))
                {
                    if (line.StartsWith("Fetching", StringComparison.Ordinal) || line.StartsWith("Error", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    listed.Add(line.Split('/', 2) is [var provider, var id]
                        ? new DiscoveredModel("opencode", line, id, provider, [])
                        : new DiscoveredModel("opencode", line, line, "opencode", []));
                }

                if (listed.Count > 0)
                {
                    return listed;
                }
            }
        }

        return [.. Catalog().Where(p => PriorityProviders.Contains(p.Provider))
            .SelectMany(p => p.Models.Select(m => new DiscoveredModel("opencode", $"{p.Provider}/{m.Id}", m.Name ?? m.Id, p.Provider, [], m.Description)))];
    }

    /// <summary>Antigravity's models, from <c>agy models</c>.</summary>
    public async Task<IReadOnlyList<DiscoveredModel>> AntigravityAsync(CancellationToken cancellationToken)
    {
        if (which("agy") is null)
        {
            return [];
        }

        var ran = await proc.RunAsync(new ProcRequest(["agy", "models"]) { Timeout = TimeSpan.FromSeconds(4) }, cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            return [];
        }

        List<DiscoveredModel> found = [];
        foreach (var line in Lines(ran.Out).Where(l => !l.Contains("Fetching", StringComparison.Ordinal)))
        {
            if (Columns().Split(line, 2) is [var id, var name])
            {
                var provider = id.Contains("claude", StringComparison.Ordinal) ? "anthropic"
                    : id.Contains("gpt", StringComparison.Ordinal) ? "openai"
                    : "google";
                found.Add(new DiscoveredModel("antigravity", id.Trim(), name.Trim(), provider, []));
            }
        }

        return found;
    }

    /// <summary>Copilot's models, from the github-copilot provider in OpenCode's cache.</summary>
    public IReadOnlyList<DiscoveredModel> Copilot() =>
        [.. Catalog().Where(p => p.Provider == "github-copilot").Take(1)
            .SelectMany(p => p.Models.Select(m => new DiscoveredModel("github-copilot", m.Id, m.Name ?? m.Id, "github-copilot", [], m.Description)))];

    // The canonical agent for an alias: null when none was given, "" when it names no agent.
    private static string? AgentKey(string? agent)
    {
        var normalized = (agent ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => null,
            "claude" or "claude-code" or "ccc" => "claude-code",
            "codex" or "openai" => "codex",
            "antigravity" or "agy" or "gemini" => "antigravity",
            "opencode" or "oc" => "opencode",
            "copilot" or "github-copilot" or "cpl" => "github-copilot",
            _ => string.Empty,
        };
    }

    // OpenCode's cache: each provider with its models, in file order. A malformed file is an empty catalog.
    private List<(string Provider, List<(string Id, string? Name, string? Description)> Models)> Catalog()
    {
        if (ReadObject(OpencodeCache) is not { } cache)
        {
            return [];
        }

        List<(string, List<(string, string?, string?)>)> providers = [];
        foreach (var (provider, value) in cache)
        {
            if (value is not JsonObject catalog)
            {
                return [];
            }

            List<(string, string?, string?)> models = [];
            if (catalog["models"] is JsonObject entries)
            {
                foreach (var (id, info) in entries)
                {
                    if (info is not JsonObject model)
                    {
                        return [];
                    }

                    models.Add((id, NonEmpty(Text(model["name"])), Text(model["description"])));
                }
            }

            if (models.Count > 0)
            {
                providers.Add((provider, models));
            }
        }

        return providers;
    }

    private static JsonObject? ReadObject(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // A JSON string's value; any other kind (the Python's Lenient) is null.
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static IEnumerable<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Columns();
}
