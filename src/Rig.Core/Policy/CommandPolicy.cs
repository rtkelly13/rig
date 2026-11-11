using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rig.Core.Policy;

/// <summary>
/// The estate command policy (<c>config/cli-allowlists.json</c>): command globs by tier, and the skills allowlist.
/// Ported from <c>rig.policy.engine</c>.
/// </summary>
public sealed class CommandPolicy
{
    private static readonly HashSet<string> ControlTokens = new([";", "&", "&&", "|", "||", ">", ">>", "<", "<<"], StringComparer.Ordinal);

    private readonly Regex[] _globs;
    private readonly bool _windows;

    /// <summary>A policy of <paramref name="commandRules"/>, matched as <paramref name="windows"/> matches globs.</summary>
    public CommandPolicy(IReadOnlyList<string> skillsAllowlist, IReadOnlyList<CommandRule> commandRules, bool windows)
    {
        ArgumentNullException.ThrowIfNull(commandRules);
        SkillsAllowlist = skillsAllowlist;
        CommandRules = commandRules;
        _windows = windows;
        _globs = [.. commandRules.Select(r => Glob.Compile(windows ? NormCase(r.Pattern) : r.Pattern))];
    }

    /// <summary>The skills the estate installs.</summary>
    public IReadOnlyList<string> SkillsAllowlist { get; }

    /// <summary>Every rule, tier by tier from safe to denied, each tier in file order.</summary>
    public IReadOnlyList<CommandRule> CommandRules { get; }

    /// <summary>
    /// Loads <paramref name="path"/>, failing closed: a misshapen rule throws <see cref="InvalidDataException"/> rather
    /// than being dropped, since a silently missing deny rule would let a broader allow rule match instead.
    /// </summary>
    public static CommandPolicy Load(string path, bool windows)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw Misshapen("the policy must be an object");
        var commands = root["commands"] as JsonObject ?? throw Misshapen("commands must be an object of tiers");
        var byTier = new Dictionary<CommandRisk, List<CommandRule>>();
        foreach (var (tier, entries) in commands)
        {
            var risk = PolicyNames.ParseRisk(tier) ?? throw Misshapen($"commands.{tier} is not a tier");
            var list = entries as JsonArray ?? throw Misshapen($"commands.{tier} must be a list");
            byTier[risk] = [.. list.Select(entry => Rule(entry, risk, tier))];
        }

        var rules = Enum.GetValues<CommandRisk>().SelectMany(r => byTier.GetValueOrDefault(r) ?? []).ToList();
        return new CommandPolicy(Skills(root["skills"], path), rules, windows);
    }

    /// <summary>
    /// Classifies <paramref name="command"/> by its most specific matching rule: denied wins, then external, local and
    /// safe, then the longer pattern. A composed shell line, or one no rule matches, is denied, so a new command cannot
    /// slip past review.
    /// </summary>
    public CommandRisk Check(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!IsDirect(normalized))
        {
            return CommandRisk.Denied;
        }

        var subject = _windows ? NormCase(normalized) : normalized;
        CommandRule? best = null;
        for (var i = 0; i < CommandRules.Count; i++)
        {
            var rule = CommandRules[i];
            if (_globs[i].IsMatch(subject) && (best is null || (rule.Risk, rule.Pattern.Length).CompareTo((best.Risk, best.Pattern.Length)) > 0))
            {
                best = rule;
            }
        }

        return best?.Risk ?? CommandRisk.Denied;
    }

    /// <summary>Whether <paramref name="command"/> is one command, with no substitution, pipe, list or redirect a glob could hide.</summary>
    public static bool IsDirect(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0 || command.Contains("$(", StringComparison.Ordinal) || command.IndexOfAny(['`', '\n', '\r']) >= 0)
        {
            return false;
        }

        IReadOnlyList<string> tokens;
        try
        {
            tokens = Shlex.ShellTokens(command);
        }
        catch (FormatException)
        {
            return false;
        }

        return tokens.Count > 0 && !tokens.Any(ControlTokens.Contains);
    }

    private static string NormCase(string text) => text.ToLowerInvariant().Replace('/', '\\');

    private static InvalidDataException Misshapen(string what) => new($"Malformed command policy: {what}.");

    private static CommandRule Rule(JsonNode? entry, CommandRisk risk, string tier)
    {
        if (entry is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return new CommandRule(value.GetValue<string>(), risk);
        }

        if (entry is not JsonObject rule)
        {
            throw Misshapen($"a commands.{tier} rule must be a pattern or an object");
        }

        var pattern = Text(rule["pattern"]) ?? throw Misshapen($"a commands.{tier} rule has no pattern");
        var rationale = rule["rationale"] is null ? string.Empty : Text(rule["rationale"]) ?? throw Misshapen($"{pattern}: rationale must be text");
        var excluded = rule["exclude_systems"] is null ? [] : Texts(rule["exclude_systems"]) ?? throw Misshapen($"{pattern}: exclude_systems must be a list of names");
        return new CommandRule(pattern, risk, rationale, excluded);
    }

    private static List<string> Skills(JsonNode? skills, string path)
    {
        if (skills is null)
        {
            return [];
        }

        if (skills is not JsonObject section)
        {
            throw Misshapen("skills must be an object");
        }

        if (section["allowlist_file"] is { } file)
        {
            var name = Text(file) ?? throw Misshapen("skills.allowlist_file must be a path");
            var listed = JsonNode.Parse(File.ReadAllText(Path.Join(Path.GetDirectoryName(path), name)));
            return Texts(listed) ?? throw Misshapen($"{name} must be a list of skill names");
        }

        return section["allow"] is null ? [] : Texts(section["allow"]) ?? throw Misshapen("skills.allow must be a list of skill names");
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static List<string>? Texts(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var texts = array.Select(Text).ToList();
        return texts.Contains(null) ? null : [.. texts.OfType<string>()];
    }
}
