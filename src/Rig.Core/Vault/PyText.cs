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
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case 'İ':
                    builder.Append("i̇");
                    break;
                case 'Σ':
                    builder.Append(FinalSigma(s, i) ? 'ς' : 'σ');
                    break;
                default:
                    builder.Append(char.ToLowerInvariant(c));
                    break;
            }
        }

        return builder.ToString();
    }

    // Unicode's Final_Sigma condition: a cased letter before (past case-ignorable marks) and none after.
    private static bool FinalSigma(string s, int at)
    {
        var before = at - 1;
        while (before >= 0 && CaseIgnorable(s[before]))
        {
            before--;
        }

        if (before < 0 || !Cased(s[before]))
        {
            return false;
        }

        var after = at + 1;
        while (after < s.Length && CaseIgnorable(s[after]))
        {
            after++;
        }

        return after >= s.Length || !Cased(s[after]);
    }

    private static bool Cased(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter;

    private static bool CaseIgnorable(char c) =>
        c is '\'' or '.' or ':' or '^' or '`' or '·' or '’' or '‘' or '․' or '‧'
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
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
