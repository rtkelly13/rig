using System.Text.RegularExpressions;

namespace Rig.Core.Desktop;

/// <summary>Launch entries on one desktop that failed with the same missing-path error.</summary>
/// <param name="DesktopIndex">The desktop, from 1.</param>
/// <param name="Entries">The entries, in the order they failed.</param>
public sealed record MissingPathGroup(int DesktopIndex, IReadOnlyList<LaunchEntry> Entries);

/// <summary>
/// The launches a restore could not start because a path is missing, grouped by the missing path, so restore can open
/// one note per path afterwards. Ported from the state half of <c>desktop_manager.restore_fallbacks</c>.
/// </summary>
public sealed partial class RestoreFailureFallbacks
{
    private readonly OrderedDictionary<string, (int DesktopIndex, List<LaunchEntry> Entries)> _groups = new(StringComparer.Ordinal);

    /// <summary>Each missing-path message, with the entries that hit it, in the order first seen.</summary>
    public IReadOnlyList<KeyValuePair<string, MissingPathGroup>> MissingPathEntries =>
        [.. _groups.Select(group => KeyValuePair.Create(group.Key, new MissingPathGroup(group.Value.DesktopIndex, [.. group.Value.Entries])))];

    /// <summary>Whether any launch failed for a missing path.</summary>
    public bool HasMissingPaths => _groups.Count > 0;

    /// <summary>
    /// Record that <paramref name="entry"/> on <paramref name="desktopIndex"/> failed with <paramref name="message"/>. A
    /// path is grouped under the first desktop it failed on, and an entry is recorded once.
    /// </summary>
    public void RecordMissingPath(int desktopIndex, LaunchEntry entry, string message)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = NormalizeMissingPathErrorMessage(message);
        if (!_groups.TryGetValue(key, out var group))
        {
            group = (desktopIndex, []);
            _groups[key] = group;
        }

        if (group.DesktopIndex == desktopIndex && !group.Entries.Exists(existing => existing.Name == entry.Name))
        {
            group.Entries.Add(entry);
        }
    }

    /// <summary>Whether a launch failure's message says a resolved path does not exist.</summary>
    public static bool IsMissingPathError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Contains("resolved path does not exist", StringComparison.Ordinal)
            && message.Contains("Launch entry `", StringComparison.Ordinal);
    }

    /// <summary>A missing-path message with the entry's name taken out, so entries missing the same path group together.</summary>
    public static string NormalizeMissingPathErrorMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var match = MissingPathError().Match(message);
        return match.Success ? $"Launch target resolved {match.Groups["field"].Value} does not exist: {match.Groups["value"].Value}" : message;
    }

    // Python's re.match with $: anchored at the start, and the end may sit before one trailing newline.
    [GeneratedRegex(@"^Launch entry `(?<entry>.+?)` resolved (?<field>path|cwd|arg) does not exist: (?<value>.+)$")]
    private static partial Regex MissingPathError();
}
