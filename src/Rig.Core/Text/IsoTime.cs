using System.Globalization;

namespace Rig.Core.Text;

/// <summary>ISO 8601 text: formatted with an offset, to the second or with microseconds only when there are any, and parsed back.</summary>
public static class IsoTime
{
    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd' 'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd",
    ];

    /// <summary><paramref name="moment"/> to the second with its offset: <c>2026-11-12T09:00:00-05:00</c>.</summary>
    public static string Seconds(DateTimeOffset moment) => moment.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary><paramref name="moment"/> with six fractional digits only when the microsecond is non-zero, and its offset.</summary>
    public static string Format(DateTimeOffset moment) =>
        moment.Ticks % TimeSpan.TicksPerSecond / 10 == 0
            ? Seconds(moment)
            : moment.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    /// <summary><paramref name="moment"/> as <see cref="Format(DateTimeOffset)"/>, or null.</summary>
    public static string? Format(DateTimeOffset? moment) => moment is { } value ? Format(value) : null;

    /// <summary><paramref name="text"/> parsed, or null when it is absent or not a time.</summary>
    public static DateTimeOffset? Parse(string? text) =>
        !string.IsNullOrWhiteSpace(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    /// <summary>
    /// <paramref name="text"/> read as ISO 8601, in the forms Claude Code and a person
    /// write: a date, or a date and time with an optional fraction and an optional <c>Z</c> or offset. A time without
    /// an offset is UTC. Null when it is none of those.
    /// </summary>
    public static DateTimeOffset? ParseIso(string? text) =>
        DateTimeOffset.TryParseExact(text, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
