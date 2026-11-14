using System.Text.RegularExpressions;

namespace Rig.Core.Workload;

/// <summary>
/// Makes a command line safe to keep: secret-looking values are replaced with <c>***</c> and the result is cut to a few
/// hundred characters. Command lines carry tokens (<c>--token=…</c>, <c>Authorization: Bearer …</c>, URLs with a password),
/// so a sample never stores a whole one. Redaction runs before the cut, so a secret is never half kept.
/// </summary>
public static partial class CommandLines
{
    /// <summary>What a redacted value becomes.</summary>
    public const string Mask = "***";

    /// <summary><paramref name="command"/> redacted, its whitespace collapsed, and cut to <paramref name="max"/> characters (ending in <c>…</c> when cut).</summary>
    public static string Safe(string command, int max)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = Whitespace().Replace(command, " ").Trim();
        text = UrlCredentials().Replace(text, "${scheme}" + Mask + "@");
        text = Scheme().Replace(text, "${scheme} " + Mask);
        text = KnownToken().Replace(text, Mask);
        text = Assigned().Replace(text, "${key}${sep}" + Mask);
        text = Flagged().Replace(text, "${flag} " + Mask);
        text = Opaque().Replace(text, Mask);
        return text.Length <= max ? text : string.Concat(text.AsSpan(0, Math.Max(0, max - 1)), "…");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // scheme://user:password@host
    [GeneratedRegex(@"(?<scheme>[a-z][a-z0-9+.-]*://)[^\s/@:]+:[^\s/@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    // Authorization: Bearer abc, token abc
    [GeneratedRegex(@"(?<scheme>\b(?:bearer|basic|token))\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();

    // Tokens with a well-known shape: GitHub, OpenAI/Anthropic-style keys, Slack, AWS access keys, JWTs.
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{16,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[A-Z0-9]{16}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+)")]
    private static partial Regex KnownToken();

    // key=value and key: value where the key names a secret: --token=x, password=x, api_key: x, AWS_SECRET_ACCESS_KEY=x.
    [GeneratedRegex(@"(?<key>[\w.-]*(?:token|secret|passw(?:or)?d|pwd|key|auth|credential|cookie|signature|sig)[\w.-]*)(?<sep>\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex Assigned();

    // --token value, -p value
    [GeneratedRegex(@"(?<flag>(?<!\S)--?[\w.-]*(?:token|secret|passw(?:or)?d|pwd|api-?key|auth|credential)[\w.-]*)\s+(?:""[^""]*""|'[^']*'|[^\s""'-][^\s""']*)", RegexOptions.IgnoreCase)]
    private static partial Regex Flagged();

    // A long run of token characters with no path separator: a key or hash someone passed inline.
    [GeneratedRegex(@"(?<![\w/\\.-])[A-Za-z0-9_+=-]{40,}(?![\w/\\.-])")]
    private static partial Regex Opaque();
}
