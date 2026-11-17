namespace Rig.Core.WorkItems;

/// <summary>
/// Path comparison and normalisation as Python's <c>os.path.normcase(os.path.normpath(...))</c> gives it: separators
/// folded to the platform's, <c>.</c> and <c>..</c> collapsed, and case folded on Windows only.
/// </summary>
internal static class WorkPaths
{
    /// <summary>The comparison of two normalised paths.</summary>
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The comparer for normalised paths.</summary>
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary><paramref name="path"/> in the form used for prefix comparison.</summary>
    public static string Normalise(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var separator = Path.DirectorySeparatorChar;
        var replaced = separator == '/' ? path : path.Replace('/', separator);
        var rooted = replaced.Length > 0 && replaced[0] == separator;
        var parts = new List<string>();
        foreach (var part in replaced.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..")
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (!rooted)
                {
                    parts.Add(part);
                }

                continue;
            }

            parts.Add(part);
        }

        var joined = string.Join(separator, parts);
        var normalised = rooted ? separator + joined : joined.Length > 0 ? joined : ".";
        return OperatingSystem.IsWindows() ? normalised.ToLowerInvariant() : normalised;
    }

    /// <summary>Whether <paramref name="target"/> is <paramref name="folder"/> or lies inside it; both normalised.</summary>
    public static bool IsUnder(string target, string folder)
    {
        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar);
        return target.Equals(trimmed, Comparison)
            || target.StartsWith(trimmed + Path.DirectorySeparatorChar, Comparison);
    }
}

/// <summary>
/// Times in the JSON schema, formatted as Python's <c>datetime.isoformat()</c>: six fractional digits only when the
/// microsecond is non-zero, and an offset such as <c>+00:00</c>.
/// </summary>
internal static class IsoTime
{
    /// <summary><paramref name="moment"/> as ISO 8601 with its offset, or null.</summary>
    public static string? Format(DateTimeOffset? moment)
    {
        if (moment is not { } value)
        {
            return null;
        }

        var microseconds = value.Ticks % TimeSpan.TicksPerSecond / 10;
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var fraction = microseconds == 0
            ? string.Empty
            : "." + microseconds.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return text + fraction + value.ToString("zzz", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary><paramref name="text"/> parsed, or null when it is absent or not a time.</summary>
    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd' 'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd",
    ];

    /// <summary>
    /// <paramref name="text"/> read as Python's <c>datetime.fromisoformat</c> reads the forms Claude Code and a person
    /// write: a date, or a date and time with an optional fraction and an optional <c>Z</c> or offset. A time without
    /// an offset is UTC. Null when it is none of those.
    /// </summary>
    public static DateTimeOffset? ParseIso(string? text) =>
        DateTimeOffset.TryParseExact(
            text,
            IsoFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
}
