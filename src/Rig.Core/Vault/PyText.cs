using System.Globalization;
using System.Text;

namespace Rig.Core.Vault;

/// <summary>
/// Python 3 <c>str</c> semantics where .NET's differ, so text the vault's Python tooling wrote is read and rewritten the same
/// way. Lengths and slices count code points, as Python does, not UTF-16 units.
/// </summary>
public static class PyText
{
    /// <summary>
    /// A regex class body matching what Python's <c>\s</c> and <c>str.isspace</c> match: .NET's <c>\s</c> plus the four
    /// information separators U+001C to U+001F.
    /// </summary>
    public const string Space = @"\s\x1c-\x1f";

    /// <summary>Whether <paramref name="c"/> is whitespace to Python.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary>Python's <c>s.strip()</c>: whitespace off both ends.</summary>
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

    /// <summary>Python's <c>s.strip(chars)</c>: any of <paramref name="chars"/> off both ends.</summary>
    public static string Strip(string s, string chars)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(chars);
        return s.Trim(chars.ToCharArray());
    }

    /// <summary>
    /// Python's <c>s.lower()</c>: the invariant simple mapping, plus the two full mappings Python applies that .NET does not.
    /// U+0130 becomes <c>i</c> and a combining dot, and a capital sigma ending a word becomes final sigma.
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
                    // Per scalar, so a supplementary-plane capital (Deseret's 𐐀) lowers as Python's does.
                    builder.Append(Rune.ToLowerInvariant(rune).ToString());
                    break;
            }
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

    /// <summary>Python's <c>len(s)</c>: the number of code points.</summary>
    public static int Length(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var count = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (!(char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Orders strings as Python's <c>&lt;</c> does, by code point. Ordinal comparison orders UTF-16 units, which puts a
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

    /// <summary>Python's <c>s[:count]</c>: the first <paramref name="count"/> code points.</summary>
    public static string Prefix(string s, int count)
    {
        ArgumentNullException.ThrowIfNull(s);
        var taken = 0;
        var i = 0;
        while (i < s.Length && taken < count)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            taken++;
        }

        return s[..i];
    }
}
