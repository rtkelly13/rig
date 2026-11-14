using System.Text;
using System.Text.Json.Nodes;

namespace Rig.Core.Workload;

/// <summary>One app the sampler attributes memory to.</summary>
/// <param name="App">Its name in samples, e.g. <c>Claude Code</c>.</param>
/// <param name="Names">Executable names that are this app, without <c>.exe</c>, compared ignoring case.</param>
/// <param name="Paths">Substrings of an executable path that make it this app, compared ignoring case.</param>
/// <param name="Sessions">Whether this app's top-level processes are counted as sessions.</param>
public sealed record AppRule(string App, IReadOnlyList<string> Names, IReadOnlyList<string> Paths, bool Sessions);

/// <summary>
/// The app list (<c>workload-apps.json</c>, embedded): which executables are which app, which app's processes are
/// sessions, and which names always run many copies and so are never a burst. Data, so adding an app is a JSON edit.
/// </summary>
/// <param name="Apps">The apps, in match order: the first that matches a process wins.</param>
/// <param name="BurstIgnore">Names never reported as a burst; a trailing <c>*</c> matches a prefix.</param>
public sealed record AppRules(IReadOnlyList<AppRule> Apps, IReadOnlyList<string> BurstIgnore)
{
    private static readonly Lazy<AppRules> Embedded = new(() =>
    {
        using var stream = typeof(AppRules).Assembly.GetManifestResourceStream("Rig.Core.Workload.workload-apps.json")
            ?? throw new InvalidOperationException("workload-apps.json is not embedded in Rig.Core.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>The app list rig ships.</summary>
    public static AppRules Bundled => Embedded.Value;

    /// <summary>Reads an app list in the <c>workload-apps.json</c> shape.</summary>
    /// <exception cref="System.Text.Json.JsonException">The text is not JSON.</exception>
    /// <exception cref="InvalidOperationException">An entry has no <c>app</c>.</exception>
    public static AppRules Parse(string json)
    {
        var root = JsonNode.Parse(json);
        var apps = root?["apps"] is JsonArray entries
            ? entries.Select(entry => entry is JsonObject fields && (string?)fields["app"] is { } app
                ? new AppRule(app, Strings(fields["names"]), Strings(fields["paths"]), fields["sessions"] is JsonValue flag && flag.GetValue<bool>())
                : throw new InvalidOperationException("every entry in workload-apps.json needs an app")).ToList()
            : [];
        return new AppRules(apps, Strings(root?["burst_ignore"]));
    }

    /// <summary><paramref name="name"/> without a trailing <c>.exe</c>.</summary>
    public static string Stem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The first app <paramref name="process"/> is, by name or path; null when none.</summary>
    public AppRule? Match(ProcessRow process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var stem = Stem(process.Name);
        return Apps.FirstOrDefault(rule =>
            rule.Names.Any(name => name.Equals(stem, StringComparison.OrdinalIgnoreCase))
            || (process.Path.Length > 0 && rule.Paths.Any(part => process.Path.Contains(part, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Whether a process named <paramref name="name"/> is never reported as a burst.</summary>
    public bool BurstIgnored(string name)
    {
        var stem = Stem(name);
        return BurstIgnore.Any(pattern => pattern.EndsWith('*')
            ? stem.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : stem.Equals(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray items ? [.. items.Select(item => (string?)item).OfType<string>()] : [];
}
