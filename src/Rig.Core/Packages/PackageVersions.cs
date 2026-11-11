using System.Numerics;
using System.Text.RegularExpressions;

namespace Rig.Core.Packages;

/// <summary>
/// Version ordering for registry versions: SemVer-ish, tolerant of NuGet's four-part and legacy forms. The core is
/// compared part by part as a variable-length list, a release sorts after its prereleases, and prerelease chunks compare
/// numeric before text.
/// </summary>
public static partial class PackageVersions
{
    /// <summary>Whether <paramref name="version"/> has no prerelease identifier: <c>1.0.0</c> and <c>1.0.0+build1</c> are stable.</summary>
    public static bool IsStable(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return !(plus < 0 ? version : version[..plus]).Contains('-', StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="manifest"/> orders after <paramref name="registry"/>.</summary>
    public static bool IsAhead(string manifest, string registry) => Compare(manifest, registry) > 0;

    /// <summary>The order of <paramref name="left"/> against <paramref name="right"/>: negative, zero or positive.</summary>
    public static int Compare(string left, string right)
    {
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);
        var core = Lexicographic(leftCore, rightCore, (a, b) => a.CompareTo(b));
        if (core != 0)
        {
            return core;
        }

        // A release (no prerelease) sorts after every prerelease of the same core.
        if (leftPre is null || rightPre is null)
        {
            return (leftPre is null).CompareTo(rightPre is null);
        }

        return Lexicographic(Chunks(leftPre), Chunks(rightPre), CompareChunk);
    }

    // The core is everything before the first '-'; a part that is not all digits counts as 0, so 1.2.3+x is 1.2.0.
    private static (List<BigInteger> Core, string? Pre) Split(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? version : version[..dash];
        var pre = dash < 0 ? null : version[(dash + 1)..];
        return ([.. core.Split('.').Select(part => IsDigits(part) ? BigInteger.Parse(part, System.Globalization.CultureInfo.InvariantCulture) : BigInteger.Zero)],
            string.IsNullOrEmpty(pre) ? null : pre);
    }

    private static List<(bool Text, BigInteger Number, string Word)> Chunks(string pre)
    {
        var chunks = new List<(bool, BigInteger, string)>();
        foreach (Match match in ChunkPattern().Matches(pre))
        {
            var clean = match.Value.Trim('.', '-', '_', '+');
            if (clean.Length == 0)
            {
                continue;
            }

            chunks.Add(IsDigits(clean)
                ? (false, BigInteger.Parse(clean, System.Globalization.CultureInfo.InvariantCulture), string.Empty)
                : (true, BigInteger.Zero, clean.ToLowerInvariant()));
        }

        return chunks;
    }

    private static int CompareChunk((bool Text, BigInteger Number, string Word) left, (bool Text, BigInteger Number, string Word) right)
    {
        var kind = left.Text.CompareTo(right.Text);
        if (kind != 0)
        {
            return kind;
        }

        var number = left.Number.CompareTo(right.Number);
        return number != 0 ? number : string.CompareOrdinal(left.Word, right.Word);
    }

    private static int Lexicographic<T>(IReadOnlyList<T> left, IReadOnlyList<T> right, Func<T, T, int> compare)
    {
        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var order = compare(left[i], right[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static bool IsDigits(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    [GeneratedRegex("[0-9]+|[^0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex ChunkPattern();
}
