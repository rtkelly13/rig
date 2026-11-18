using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>A URL split into its five parts.</summary>
/// <param name="Scheme">The scheme, lowercased, or empty.</param>
/// <param name="Netloc">Everything between <c>//</c> and the path, as written.</param>
/// <param name="Path">The path, as written.</param>
/// <param name="Query">The query, without its <c>?</c>.</param>
/// <param name="Fragment">The fragment, without its <c>#</c>.</param>
public sealed record UrlParts(string Scheme, string Netloc, string Path, string Query, string Fragment)
{
    /// <summary>
    /// The host in <see cref="Netloc"/>, lowercased up to any IPv6 zone, or empty when there is none.
    /// </summary>
    public string Hostname
    {
        get
        {
            var host = VaultUrl.HostOf(Netloc);
            var zone = host.IndexOf('%', StringComparison.Ordinal);
            return zone >= 0 ? UnicodeText.Lower(host[..zone]) + host[zone..] : UnicodeText.Lower(host);
        }
    }
}

/// <summary>
/// URL splitting, joining and query handling as plain string operations, so URLs round-trip exactly as written.
/// <see cref="System.Uri"/> normalizes too much to stand in.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054", Justification = "URLs are kept as written; System.Uri normalizes them.")]
public static partial class VaultUrl
{
    // Schemes that get a "//" when the netloc is empty.
    private static readonly HashSet<string> UsesNetloc = new(StringComparer.Ordinal)
    {
        string.Empty, "ftp", "http", "gopher", "nntp", "telnet", "imap", "wais", "file", "mms", "https", "shttp", "snews",
        "prospero", "rtsp", "rtsps", "rtspu", "rsync", "svn", "svn+ssh", "sftp", "nfs", "git", "git+ssh", "ws", "wss",
        "itms-services",
    };

    /// <summary>
    /// Splits <paramref name="url"/> into scheme, netloc, path, query and fragment, with leading C0 controls and spaces
    /// stripped and tabs and newlines removed.
    /// </summary>
    /// <exception cref="FormatException">The netloc has unbalanced or invalid brackets, or characters that NFKC turns into
    /// URL delimiters.</exception>
    public static UrlParts Split(string url)
    {
        var start = 0;
        while (start < url.Length && url[start] <= ' ')
        {
            start++;
        }

        var rest = url[start..].Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        var scheme = string.Empty;
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && char.IsAsciiLetter(rest[0]) && rest[..colon].All(IsSchemeChar))
        {
            scheme = UnicodeText.Lower(rest[..colon]);
            rest = rest[(colon + 1)..];
        }

        var netloc = string.Empty;
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var end = rest.IndexOfAny(['/', '?', '#'], 2);
            end = end < 0 ? rest.Length : end;
            netloc = rest[2..end];
            rest = rest[end..];
            var open = netloc.Contains('[', StringComparison.Ordinal);
            var close = netloc.Contains(']', StringComparison.Ordinal);
            if (open != close)
            {
                throw new FormatException("Invalid IPv6 URL");
            }

            if (open)
            {
                CheckBracketedNetloc(netloc);
            }
        }

        var fragment = string.Empty;
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = rest[(hash + 1)..];
            rest = rest[..hash];
        }

        var query = string.Empty;
        var question = rest.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0)
        {
            query = rest[(question + 1)..];
            rest = rest[..question];
        }

        CheckNetloc(netloc);
        return new UrlParts(scheme, netloc, rest, query, fragment);
    }

    // The host of a netloc: past any userinfo, inside brackets for IPv6, before any port.
    internal static string HostOf(string netloc)
    {
        var hostInfo = netloc[(netloc.LastIndexOf('@') + 1)..];
        var open = hostInfo.IndexOf('[', StringComparison.Ordinal);
        if (open >= 0)
        {
            var bracketed = hostInfo[(open + 1)..];
            var close = bracketed.IndexOf(']', StringComparison.Ordinal);
            return close >= 0 ? bracketed[..close] : bracketed;
        }

        var colon = hostInfo.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? hostInfo[..colon] : hostInfo;
    }

    private static bool IsSchemeChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';

    private static void CheckBracketedNetloc(string netloc)
    {
        var hostAndPort = netloc[(netloc.LastIndexOf('@') + 1)..];
        var open = hostAndPort.IndexOf('[', StringComparison.Ordinal);
        var close = hostAndPort.IndexOf(']', StringComparison.Ordinal);
        if (open > 0 || (open == 0 && close >= 0 && close + 1 < hostAndPort.Length && hostAndPort[close + 1] != ':'))
        {
            throw new FormatException("Invalid IPv6 URL");
        }

        var host = HostOf(netloc);
        var valid = host.StartsWith('v')
            ? IpvFuture().IsMatch(host)
            : IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        if (!valid)
        {
            throw new FormatException($"Invalid bracketed host {host}");
        }
    }

    // A non-ASCII netloc whose NFKC form introduces a URL delimiter, such as U+2100 becoming "a/c", is rejected.
    private static void CheckNetloc(string netloc)
    {
        if (netloc.All(char.IsAscii))
        {
            return;
        }

        var bare = netloc.Replace("@", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("#", string.Empty, StringComparison.Ordinal)
            .Replace("?", string.Empty, StringComparison.Ordinal);
        var normalized = bare.Normalize(NormalizationForm.FormKC);
        if (!string.Equals(bare, normalized, StringComparison.Ordinal) && normalized.IndexOfAny(['/', '?', '#', '@', ':']) >= 0)
        {
            throw new FormatException($"netloc '{netloc}' contains invalid characters under NFKC normalization");
        }
    }

    /// <summary>Joins <paramref name="parts"/> back into a URL as <c>urlunsplit</c> does.</summary>
    public static string Unsplit(UrlParts parts)
    {
        var url = parts.Path;
        if (parts.Netloc.Length > 0)
        {
            if (url.Length > 0 && url[0] != '/')
            {
                url = "/" + url;
            }

            url = "//" + parts.Netloc + url;
        }
        else if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "//" + url;
        }
        else if (parts.Scheme.Length > 0 && UsesNetloc.Contains(parts.Scheme) && (url.Length == 0 || url[0] == '/'))
        {
            url = "//" + url;
        }

        if (parts.Scheme.Length > 0)
        {
            url = parts.Scheme + ":" + url;
        }

        if (parts.Query.Length > 0)
        {
            url += "?" + parts.Query;
        }

        if (parts.Fragment.Length > 0)
        {
            url += "#" + parts.Fragment;
        }

        return url;
    }

    /// <summary>
    /// The query's values grouped by name, in order of first appearance: fields split on <c>&amp;</c>, those with an empty
    /// value or no <c>=</c> dropped, and <c>+</c> read as a space.
    /// </summary>
    public static IEnumerable<IGrouping<string, string>> ParseQuery(string query)
    {
        return query.Split('&')
            .Select(field => (Eq: field.IndexOf('=', StringComparison.Ordinal), Field: field))
            .Where(f => f.Eq >= 0 && f.Eq < f.Field.Length - 1)
            .GroupBy(f => Unquote(f.Field[..f.Eq].Replace('+', ' ')), f => Unquote(f.Field[(f.Eq + 1)..].Replace('+', ' ')), StringComparer.Ordinal);
    }

    /// <summary>Each value as its own <c>name=value</c>, both quoted by <see cref="QuotePlus"/>.</summary>
    public static string Encode(IEnumerable<IGrouping<string, string>> fields)
    {
        return string.Join('&', fields.SelectMany(field => field.Select(value => QuotePlus(field.Key) + "=" + QuotePlus(value))));
    }

    /// <summary>
    /// Form-style quoting with no extra safe characters: UTF-8 bytes outside <c>A-Za-z0-9_.-~</c> become <c>%XX</c>, and
    /// spaces become <c>+</c>.
    /// </summary>
    private static string QuotePlus(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~')
            {
                builder.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>%XX</c> escapes decoded as UTF-8, invalid sequences replaced. Escapes are decoded only
    /// within runs of ASCII, and characters outside ASCII are kept as written.
    /// </summary>
    public static string Unquote(string text)
    {
        if (!text.Contains('%', StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsAscii(c))
            {
                Flush();
                result.Append(c);
            }
            else if (c == '%' && i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
            {
                bytes.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                i += 2;
            }
            else
            {
                bytes.Add((byte)c);
            }
        }

        Flush();
        return result.ToString();

        void Flush()
        {
            result.Append(Encoding.UTF8.GetString([.. bytes]));
            bytes.Clear();
        }
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);

    [GeneratedRegex(@"\Av[a-fA-F0-9]+\..+\z")]
    private static partial Regex IpvFuture();
}
