using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Core.Policy;

/// <summary>
/// Python's <c>fnmatch.fnmatch</c>: <c>*</c>, <c>?</c> and <c>[seq]</c>/<c>[!seq]</c> over the whole name, with
/// <c>*</c> crossing spaces and slashes. Case-sensitive, except on Windows, where both sides are case-folded and
/// <c>/</c> read as <c>\</c> first, as <c>os.path.normcase</c> does there.
/// </summary>
public static class Glob
{
    /// <summary>Whether <paramref name="name"/> matches <paramref name="pattern"/>.</summary>
    public static bool Matches(string name, string pattern, bool windows)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(pattern);
        return windows
            ? Compile(NormCase(pattern)).IsMatch(NormCase(name))
            : Compile(pattern).IsMatch(name);
    }

    /// <summary><paramref name="pattern"/> as an anchored regular expression, translated as <c>fnmatch.translate</c> does.</summary>
    public static Regex Compile(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var regex = new StringBuilder(@"\A(?:");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    _ = regex.Append(".*");
                    break;
                case '?':
                    _ = regex.Append('.');
                    break;
                case '[':
                    i = Set(pattern, i, regex);
                    break;
                default:
                    _ = regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return new Regex(regex.Append(@")\z").ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private static string NormCase(string text) => text.ToLowerInvariant().Replace('/', '\\');

    // A bracket set starting at `open`; returns the index of its closing `]`, or `open` when unclosed (a literal `[`).
    private static int Set(string pattern, int open, StringBuilder regex)
    {
        var j = open + 1;
        if (j < pattern.Length && pattern[j] == '!')
        {
            j++;
        }

        if (j < pattern.Length && pattern[j] == ']')
        {
            j++;
        }

        var close = pattern.IndexOf(']', j);
        if (close < 0)
        {
            _ = regex.Append(@"\[");
            return open;
        }

        var body = pattern[(open + 1)..close];
        var negated = body.StartsWith('!');
        if (negated)
        {
            body = body[1..];
        }

        _ = regex.Append('[');
        if (negated)
        {
            _ = regex.Append('^');
        }
        else if (body.StartsWith('^'))
        {
            _ = regex.Append('\\');
        }

        _ = regex.Append(body.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("[", @"\[", StringComparison.Ordinal)).Append(']');
        return close;
    }
}
