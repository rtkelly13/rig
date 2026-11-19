using System.Globalization;
using System.Text;

namespace Rig.Core.Vault;

/// <summary>
/// Unicode text rules where .NET's defaults differ, so vault text written by the earlier tooling is read and rewritten the
/// same way: whitespace includes U+001C to U+001F, lengths and slices count code points rather than UTF-16 units, and
/// ordering is by code point.
/// </summary>
public static class UnicodeText
{
    /// <summary>
    /// A regex class body for whitespace: .NET's <c>\s</c> plus the four information separators U+001C to U+001F.
    /// </summary>
    public const string Space = @"\s\x1c-\x1f";

    /// <summary>Whether <paramref name="c"/> is whitespace, including U+001C to U+001F.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary><paramref name="s"/> with whitespace (see <see cref="IsSpace"/>) off both ends.</summary>
    public static string Strip(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var start = 0;
        var end = s.Length;
        while (start < end && IsSpace(s[start]))
        {
            start++;
        }

        while (end > start && IsSpace(s[end - 1]))
        {
            end--;
        }

        return s[start..end];
    }

    /// <summary><paramref name="s"/> with any of <paramref name="chars"/> off both ends.</summary>
    public static string Strip(string s, string chars)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(chars);
        return s.Trim(chars.ToCharArray());
    }

    /// <summary>
    /// Lower case by the invariant simple mapping, plus the two full mappings .NET does not apply: U+0130 becomes <c>i</c>
    /// and a combining dot, and a capital sigma ending a word becomes final sigma.
    /// </summary>
    public static string Lower(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var builder = new StringBuilder(s.Length);
        var runes = s.EnumerateRunes().ToArray();
        for (var i = 0; i < runes.Length; i++)
        {
            var rune = runes[i];
            switch (rune.Value)
            {
                case 'İ':
                    builder.Append("i̇");
                    break;
                case 'Σ':
                    builder.Append(FinalSigma(runes, i) ? 'ς' : 'σ');
                    break;
                default:
                    // Per scalar, so a supplementary-plane capital (Deseret's 𐐀) lowers correctly.
                    builder.Append(Rune.ToLowerInvariant(rune));
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Case folding for the text met here: <see cref="Lower"/> plus the full and special foldings
    /// of sharp s, the Latin ligatures, final sigma, long s, micro and the Greek symbol variants. Rarer full foldings
    /// (precomposed Greek with iota, Armenian ligatures) are left as <see cref="Lower"/> gives them.
    /// </summary>
    public static string CaseFold(string s)
    {
        var lower = Lower(s);
        var builder = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            builder.Append(c switch
            {
                'ß' => "ss",
                'ς' => "σ",
                'ſ' => "s",
                'µ' => "μ",
                'ϐ' => "β",
                'ϑ' => "θ",
                'ϕ' => "φ",
                'ϖ' => "π",
                'ϰ' => "κ",
                'ϱ' => "ρ",
                'ϵ' => "ε",
                'ﬀ' => "ff",
                'ﬁ' => "fi",
                'ﬂ' => "fl",
                'ﬃ' => "ffi",
                'ﬄ' => "ffl",
                'ﬅ' or 'ﬆ' => "st",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    // Unicode's Final_Sigma condition: a cased letter before (past case-ignorable marks) and none after.
    private static bool FinalSigma(Rune[] runes, int at)
    {
        var before = at - 1;
        while (before >= 0 && CaseIgnorable(runes[before]))
        {
            before--;
        }

        if (before < 0 || !Cased(runes[before]))
        {
            return false;
        }

        var after = at + 1;
        while (after < runes.Length && CaseIgnorable(runes[after]))
        {
            after++;
        }

        return after >= runes.Length || !Cased(runes[after]);
    }

    private static bool Cased(Rune r) =>
        Rune.GetUnicodeCategory(r) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter;

    private static bool CaseIgnorable(Rune r) =>
        r.Value is '\'' or '.' or ':' or '^' or '`' or '·' or '’' or '‘' or '․' or '‧'
        || Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol;

    /// <summary>The number of code points in <paramref name="s"/>.</summary>
    public static int Length(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.EnumerateRunes().Count();
    }

    /// <summary>
    /// Orders strings by code point. Ordinal comparison orders UTF-16 units, which puts a
    /// supplementary-plane character (a surrogate pair) before U+E000 to U+FFFF.
    /// </summary>
    public static IComparer<string> CodePointOrder { get; } = Comparer<string>.Create(CompareCodePoints);

    private static int CompareCodePoints(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return CodePointRank(a[i]).CompareTo(CodePointRank(b[i]));
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    // Surrogates rank above U+E000 to U+FFFF, as the code points they encode do.
    private static int CodePointRank(char c) => c switch
    {
        >= '\uE000' => c - 0x800,
        >= '\uD800' => c + 0x2000,
        _ => c,
    };

    /// <summary>
    /// Splits at <c>\n</c>, <c>\r</c>, <c>\r\n</c> and the eight other Unicode line boundaries
    /// (<c>\v \f \x1c \x1d \x1e \x85 \u2028 \u2029</c>); a trailing boundary ends the last line rather than starting an
    /// empty one.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                lines.Add(s[start..i]);
                if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (start < s.Length)
        {
            lines.Add(s[start..]);
        }

        return lines;
    }

    /// <summary>The first <paramref name="count"/> code points of <paramref name="s"/>.</summary>
    public static string Prefix(string s, int count)
    {
        ArgumentNullException.ThrowIfNull(s);
        var length = 0;
        foreach (var rune in s.EnumerateRunes().Take(count))
        {
            length += rune.Utf16SequenceLength;
        }

        return s[..length];
    }
}
