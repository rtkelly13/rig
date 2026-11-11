using System.Text;

namespace Rig.Core.Policy;

/// <summary>
/// Python's POSIX <c>shlex</c> with <c>whitespace_split</c>: <see cref="Split"/> is <c>shlex.split</c>, and
/// <see cref="ShellTokens"/> is a lexer built with <c>punctuation_chars=";&amp;|&lt;&gt;"</c>, which also starts a
/// comment at <c>#</c> and returns each run of those characters as a token of its own. Throws
/// <see cref="FormatException"/> where Python raises <c>ValueError</c>: an unclosed quote or a trailing escape.
/// </summary>
public static class Shlex
{
    private const string Punctuation = ";&|<>";
    private const string Whitespace = " \t\r\n";

    /// <summary><c>shlex.split(text)</c>.</summary>
    public static IReadOnlyList<string> Split(string text) => Tokens(text, shell: false);

    /// <summary>The tokens of a <c>shlex.shlex(text, posix=True, punctuation_chars=";&amp;|&lt;&gt;")</c> with <c>whitespace_split</c>.</summary>
    public static IReadOnlyList<string> ShellTokens(string text) => Tokens(text, shell: true);

    // Past the end of the line a comment started on.
    private static int SkipLine(string text, int i) => text.IndexOf('\n', i) is var newline and >= 0 ? newline + 1 : text.Length;

    private static List<string> Tokens(string text, bool shell)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<string>();
        var token = new StringBuilder();
        var state = ' ';
        var escaped = 'a';
        var quoted = false;
        var i = 0;

        void Emit()
        {
            tokens.Add(token.ToString());
            _ = token.Clear();
            quoted = false;
        }

        while (true)
        {
            var c = i < text.Length ? text[i] : '\0';
            var end = i >= text.Length;
            i++;
            if (state == ' ')
            {
                if (end)
                {
                    break;
                }

                if (Whitespace.Contains(c, StringComparison.Ordinal))
                {
                    continue;
                }

                if (shell && c == '#')
                {
                    i = SkipLine(text, i);
                    continue;
                }

                if (c == '\\')
                {
                    escaped = 'a';
                    state = c;
                }
                else if (shell && Punctuation.Contains(c, StringComparison.Ordinal))
                {
                    _ = token.Append(c);
                    state = 'c';
                }
                else if (c is '\'' or '"')
                {
                    state = c;
                }
                else
                {
                    _ = token.Append(c);
                    state = 'a';
                }
            }
            else if (state is '\'' or '"')
            {
                quoted = true;
                if (end)
                {
                    throw new FormatException("No closing quotation");
                }

                if (c == state)
                {
                    state = 'a';
                }
                else if (state == '"' && c == '\\')
                {
                    escaped = state;
                    state = c;
                }
                else
                {
                    _ = token.Append(c);
                }
            }
            else if (state == '\\')
            {
                if (end)
                {
                    throw new FormatException("No escaped character");
                }

                if (escaped is '\'' or '"' && c != state && c != escaped)
                {
                    _ = token.Append(state);
                }

                _ = token.Append(c);
                state = escaped;
            }
            else if (end)
            {
                break;
            }
            else if (Whitespace.Contains(c, StringComparison.Ordinal))
            {
                state = ' ';
                Emit();
            }
            else if (shell && c == '#')
            {
                state = ' ';
                Emit();
                i = SkipLine(text, i);
            }
            else if (state == 'c')
            {
                if (Punctuation.Contains(c, StringComparison.Ordinal))
                {
                    _ = token.Append(c);
                }
                else
                {
                    i--; // read again as the start of the next token
                    state = ' ';
                    Emit();
                }
            }
            else if (c is '\'' or '"')
            {
                state = c;
            }
            else if (c == '\\')
            {
                escaped = 'a';
                state = c;
            }
            else if (!shell || !Punctuation.Contains(c, StringComparison.Ordinal))
            {
                _ = token.Append(c);
            }
            else
            {
                i--;
                state = ' ';
                Emit();
            }
        }

        if (state is 'a' or 'c' && (token.Length > 0 || quoted))
        {
            Emit();
        }

        return tokens;
    }
}
