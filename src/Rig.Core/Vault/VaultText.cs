using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>
/// The vault's text rules for whitespace, URLs and filename stems; matching goes through
/// <see cref="UnicodeText"/> and <see cref="Uri"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054", Justification = "URLs are kept as text; System.Uri reads them.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1055", Justification = "URLs are kept as text; System.Uri reads them.")]
public static partial class VaultText
{
    // Query parameters that only track where a click came from; CleanUrl drops them, ignoring case.
    private static readonly HashSet<string> TrackingParameters = new(StringComparer.Ordinal)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "ref", "ref_src", "ref_url", "s", "t",
        "fbclid", "gclid", "msclkid", "mc_cid", "mc_eid", "oai-dm", "source",
    };

    /// <summary>Whitespace runs collapsed to one space, and the ends stripped.</summary>
    public static string CleanWhitespace(string? text) => UnicodeText.Strip(Whitespace().Replace(text ?? string.Empty, " "));

    /// <summary>
    /// A stable form of <paramref name="url"/> for spotting duplicates. Tracking parameters, the fragment, a trailing slash,
    /// <c>www.</c>, any user or port are dropped, the scheme and host are lowercased, and X and Twitter status links become
    /// <c>https://x.com/i/status/ID</c>. Text that is not a URL with a host is returned as written.
    /// </summary>
    /// <exception cref="FormatException">The text opens like an http or https URL whose host is not one.</exception>
    public static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var text = CleanUrl(UnicodeText.Strip(url));
        if (!TryHttp(text, out var uri))
        {
            return BrokenAuthority().IsMatch(text) ? throw new FormatException($"not a URL: {url}") : text;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
        var normalized = $"{uri.Scheme}://{host}{uri.AbsolutePath.TrimEnd('/')}{uri.Query}";
        return TwitterStatus().Replace(normalized, "https://x.com/i/status/$1");
    }

    /// <summary>Whether <paramref name="url"/> is an http or https URL; a malformed one is not.</summary>
    public static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary><paramref name="url"/> without its tracking parameters or empty ones; everything else is kept as written.</summary>
    public static string CleanUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var at = url.IndexOf('?', StringComparison.Ordinal);
        if (at < 0)
        {
            return url;
        }

        var fragment = url.IndexOf('#', at, StringComparison.Ordinal);
        var query = url[(at + 1)..(fragment < 0 ? url.Length : fragment)];
        var kept = query.Split('&').Where(field =>
            field.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals
            && equals < field.Length - 1
            && !TrackingParameters.Contains(UnicodeText.Lower(Uri.UnescapeDataString(field[..equals])))).ToList();
        return url[..at] + (kept.Count > 0 ? "?" + string.Join('&', kept) : string.Empty) + (fragment < 0 ? string.Empty : url[fragment..]);
    }

    private static bool TryHttp(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(text, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;

    /// <summary>The lowercase host and the unescaped path segments of <paramref name="url"/>; a text with no scheme is all path.</summary>
    internal static (string Host, IReadOnlyList<string> Segments) HostAndPath(string url)
    {
        var absolute = TryHttp(url.StartsWith("//", StringComparison.Ordinal) ? "http:" + url : url, out var uri);
        if (!absolute && BrokenAuthority().IsMatch(url))
        {
            throw new FormatException($"not a URL: {url}");
        }

        var path = absolute ? uri!.AbsolutePath : SchemePrefix().Replace(url.Split('?', '#')[0], string.Empty);
        return (absolute ? uri!.Host : string.Empty, [.. path.Split('/').Where(part => part.Length > 0).Select(Uri.UnescapeDataString)]);
    }

    /// <summary>
    /// A filename stem safe on every platform: no reserved characters or dots, single spaces and hyphens, at most
    /// <paramref name="maxLength"/> code points, and <c>Untitled</c> when nothing is left.
    /// </summary>
    public static string SanitizeFilename(string? stem, int maxLength = 120)
    {
        var s = UnicodeText.Strip(stem ?? string.Empty);
        s = ReservedFilenameChars().Replace(s, "-");
        s = Whitespace().Replace(s, " ");
        s = UnicodeText.Strip(s, " .");
        s = RepeatedDots().Replace(s, ".");
        s = s.Replace('.', '-');
        s = UnicodeText.Strip(RepeatedHyphens().Replace(s, "-"), "-");
        if (s.Length == 0)
        {
            s = "Untitled";
        }

        if (UnicodeText.Length(s) > maxLength)
        {
            s = UnicodeText.Prefix(s, maxLength).TrimEnd(' ', '.', '-');
        }

        return s;
    }

    /// <summary>Whether <paramref name="value"/> reads as a URL or a domain-style label such as <c>example.com/page</c>.</summary>
    public static bool LooksLikeUrl(string? value)
    {
        var s = CleanWhitespace(value);
        return s.Length > 0 && (IsHttpUrl(s) || DomainLike().IsMatch(UnicodeText.Lower(s)));
    }

    /// <summary>Whether a filename stem still looks like a URL after sanitizing, such as <c>https---example.com-page</c>.</summary>
    public static bool LooksLikeUrlishStem(string? value)
    {
        var s = UnicodeText.Lower(UnicodeText.Strip(CleanWhitespace(value), "-_. "));
        if (s.Length == 0)
        {
            return false;
        }

        return IsHttpUrl(s) || s.StartsWith("www.", StringComparison.Ordinal)
            || HttpToken().IsMatch(s) || WwwToken().IsMatch(s) || DomainToken().IsMatch(s);
    }

    /// <summary>
    /// A compact lowercase ASCII stem: accents folded, <c>&amp;</c> read as <c>and</c>, apostrophes dropped, every other run
    /// of punctuation one hyphen, and at most <paramref name="maxLength"/> characters.
    /// </summary>
    public static string SlugifyNoteStem(string? value, int maxLength = 80)
    {
        var s = CleanWhitespace(value);
        if (s.Length == 0)
        {
            return "untitled";
        }

        s = s.Replace("&", " and ", StringComparison.Ordinal).Replace('_', ' ');
        s = AsciiOnly(s.Normalize(NormalizationForm.FormKD));
        s = Apostrophes().Replace(s, string.Empty);
        s = NonAlphanumeric().Replace(s, "-");
        s = UnicodeText.Lower(UnicodeText.Strip(RepeatedHyphens().Replace(s, "-"), "-"));
        if (s.Length == 0)
        {
            return "untitled";
        }

        if (s.Length > maxLength)
        {
            s = s[..maxLength].TrimEnd('-');
        }

        return s.Length == 0 ? "untitled" : s;
    }

    // Drops every non-ASCII character.
    private static string AsciiOnly(string s)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var c in s.Where(char.IsAscii))
        {
            builder.Append(c);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^https?://[^/?#]*[\[\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrokenAuthority();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemePrefix();

    [GeneratedRegex($"[{UnicodeText.Space}]+")]
    internal static partial Regex Whitespace();

    [GeneratedRegex(@"https?://(?:x|twitter)\.com/[^/]+/status/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TwitterStatus();

    [GeneratedRegex("""[\\/:*?"<>|]""")]
    private static partial Regex ReservedFilenameChars();

    [GeneratedRegex(@"\.{2,}")]
    private static partial Regex RepeatedDots();

    [GeneratedRegex("-{2,}")]
    internal static partial Regex RepeatedHyphens();

    [GeneratedRegex(@"^(?:www\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)+(?:[/?#].*)?$")]
    private static partial Regex DomainLike();

    [GeneratedRegex("(?:^|[-_. ])https?(?:[-_. ]|$)")]
    private static partial Regex HttpToken();

    [GeneratedRegex("(?:^|[-_. ])www(?:[-_. ]|$)")]
    private static partial Regex WwwToken();

    [GeneratedRegex("(?:^|[^a-z0-9])(?:[a-z0-9-]+(?:\\.[a-z0-9-]+)+)(?:$|[^a-z0-9])")]
    private static partial Regex DomainToken();

    [GeneratedRegex("['’]")]
    private static partial Regex Apostrophes();

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
