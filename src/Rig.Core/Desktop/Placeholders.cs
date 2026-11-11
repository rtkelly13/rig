using System.Text.RegularExpressions;

namespace Rig.Core.Desktop;

/// <summary><c>{name}</c> placeholders in plan values. Ported from <c>desktop_manager.placeholders</c> and the resolution in <c>desktop_manager.models</c>.</summary>
public static partial class Placeholders
{
    /// <summary>Placeholders every plan can use, read from the environment variable named, where it is set.</summary>
    public static readonly IReadOnlyList<(string Name, string Variable)> Environment =
    [
        ("localappdata", "LOCALAPPDATA"),
        ("appdata", "APPDATA"),
        ("userprofile", "USERPROFILE"),
        ("programfiles", "ProgramFiles"),
    ];

    /// <summary><see cref="Environment"/> resolved through <paramref name="getVariable"/>; unset or empty variables are left out.</summary>
    public static OrderedDictionary<string, string> EnvironmentAliases(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var aliases = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, variable) in Environment)
        {
            if (getVariable(variable) is { Length: > 0 } value)
            {
                aliases[name] = value;
            }
        }

        return aliases;
    }

    /// <summary>
    /// <paramref name="value"/> with every <c>{name}</c> replaced from <paramref name="substitutions"/>, in their order.
    /// Throws <see cref="InvalidPlanException"/> naming any placeholder left unresolved.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    public static string? Substitute(string? value, IEnumerable<KeyValuePair<string, string>> substitutions)
    {
        ArgumentNullException.ThrowIfNull(substitutions);
        if (value is null)
        {
            return null;
        }

        foreach (var (name, replacement) in substitutions)
        {
            value = value.Replace("{" + name + "}", replacement, StringComparison.Ordinal);
        }

        var unresolved = Pattern().Matches(value).Select(match => match.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return unresolved.Count > 0
            ? throw new InvalidPlanException($"Unresolved placeholders: {string.Join(", ", unresolved)}")
            : value;
    }

    /// <summary>
    /// Each value of <paramref name="values"/> with the placeholders naming other keys expanded, recursively. A cycle,
    /// a self-reference or an unknown name is left as written.
    /// </summary>
    public static OrderedDictionary<string, string> ResolveMap(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var resolved = new OrderedDictionary<string, string>(StringComparer.Ordinal);

        string ResolveKey(string key, IReadOnlyList<string> stack)
        {
            if (resolved.TryGetValue(key, out var done))
            {
                return done;
            }

            var raw = values.GetValueOrDefault(key, string.Empty);
            if (stack.Contains(key))
            {
                return raw;
            }

            var value = Pattern().Replace(raw, match =>
            {
                var placeholder = match.Groups[1].Value;
                return placeholder == key || !values.ContainsKey(placeholder) ? match.Value : ResolveKey(placeholder, [.. stack, key]);
            });
            resolved[key] = value;
            return value;
        }

        foreach (var key in values.Keys)
        {
            _ = ResolveKey(key, []);
        }

        return resolved;
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Pattern();
}
