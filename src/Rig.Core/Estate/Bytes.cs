using System.Globalization;

namespace Rig.Core.Estate;

/// <summary>Byte counts as a table shows them, and sizes as a flag takes them (<c>500MB</c>, <c>2GB</c>).</summary>
public static class Bytes
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    private static readonly (string Suffix, long Factor)[] Suffixes =
    [
        ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("B", 1),
    ];

    /// <summary><paramref name="size"/> at one decimal place in the largest unit up to <c>TB</c>; whole bytes below a kilobyte.</summary>
    public static string Human(long size) => Format(size, Units.Length);

    /// <summary>As <see cref="Human"/>, but stopping at <c>GB</c>: how the per-clone checks word a scratch entry's size.</summary>
    public static string HumanToGb(long size) => Format(size, Units.Length - 1);

    /// <summary>
    /// <c>500MB</c>, <c>2gb</c>, <c>1.5 GB</c> or a plain byte count, in 1024s; zero for anything else, so a typo filters nothing out.
    /// </summary>
    public static long ParseSize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var raw = text.Trim().ToUpperInvariant();
        foreach (var (suffix, factor) in Suffixes)
        {
            if (raw.EndsWith(suffix, StringComparison.Ordinal))
            {
                var head = raw[..^suffix.Length].Trim();
                return double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                    ? (long)(value * factor)
                    : 0;
            }
        }

        return raw.Length > 0 && raw.All(char.IsAsciiDigit) && long.TryParse(raw, CultureInfo.InvariantCulture, out var plain) ? plain : 0;
    }

    private static string Format(long size, int units)
    {
        var value = (double)size;
        for (var unit = 0; unit < units; unit++)
        {
            if (value < 1024 || unit == units - 1)
            {
                return unit == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{value:0}B")
                    : string.Create(CultureInfo.InvariantCulture, $"{value:0.0}{Units[unit]}");
            }

            value /= 1024;
        }

        return string.Empty;
    }
}
