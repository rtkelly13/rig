using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>
/// The vault's text rules for whitespace, URLs and filename stems, ported from <c>rig.obsidian.utils</c>. Matching is
/// Python's, through <see cref="PyText"/> and <see cref="PyUrl"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054", Justification = "URLs are kept as written; System.Uri normalizes them.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1055", Justification = "URLs are kept as written; System.Uri normalizes them.")]
public static partial class VaultText
{
    // Query parameters that only track where a click came from; clean_url drops them, ignoring case.
    private static readonly HashSet<string> TrackingParameters = new(StringComparer.Ordinal)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "ref", "ref_src", "ref_url", "s", "t",
        "fbclid", "gclid", "msclkid", "mc_cid", "mc_eid", "oai-dm", "source",
    };

    /// <summary>Whitespace runs collapsed to one space, and the ends stripped.</summary>
    public static string CleanWhitespace(string? text) => PyText.Strip(Whitespace().Replace(text ?? string.Empty, " "));

    /// <summary>
    /// A stable form of <paramref name="url"/> for spotting duplicates. Tracking parameters, the fragment, a trailing slash,
    /// <c>www.</c>, any user or port are dropped, the scheme and host are lowercased, and X and Twitter status links become
    /// <c>https://x.com/i/status/ID</c>.
    /// </summary>
    /// <exception cref="FormatException">The URL's netloc is malformed (see <see cref="PyUrl.Split"/>).</exception>
    public static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var parts = PyUrl.Split(CleanUrl(PyText.Strip(url)));
        var host = PyText.Lower(parts.Hostname);
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        var normalized = PyUrl.Unsplit(new UrlParts(PyText.Lower(parts.Scheme), host, parts.Path.TrimEnd('/'), parts.Query, string.Empty));
        return TwitterStatus().Replace(normalized, "https://x.com/i/status/$1");
    }

    /// <summary>Whether <paramref name="url"/> is an http or https URL; a malformed one is not.</summary>
    public static bool IsHttpUrl(string url)
    {
        try
        {
            return PyUrl.Split(url).Scheme is "http" or "https";
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary><paramref name="url"/> without tracking parameters or empty ones; everything else is kept as written.</summary>
    /// <exception cref="FormatException">The URL's netloc is malformed (see <see cref="PyUrl.Split"/>).</exception>
    public static string CleanUrl(string url)
    {
        var parts = PyUrl.Split(url);
        var kept = PyUrl.ParseQuery(parts.Query).Where(field => !TrackingParameters.Contains(PyText.Lower(field.Key)));
        return PyUrl.Unsplit(parts with { Query = PyUrl.Encode(kept) });
    }

    /// <summary>
    /// A filename stem safe on every platform: no reserved characters or dots, single spaces and hyphens, at most
    /// <paramref name="maxLength"/> code points, and <c>Untitled</c> when nothing is left.
    /// </summary>
    public static string SanitizeFilename(string? stem, int maxLength = 120)
    {
        var s = PyText.Strip(stem ?? string.Empty);
        s = ReservedFilenameChars().Replace(s, "-");
        s = Whitespace().Replace(s, " ");
        s = PyText.Strip(s, " .");
        s = RepeatedDots().Replace(s, ".");
        s = s.Replace('.', '-');
        s = PyText.Strip(RepeatedHyphens().Replace(s, "-"), "-");
        if (s.Length == 0)
        {
            s = "Untitled";
        }

        if (PyText.Length(s) > maxLength)
        {
            s = PyText.Prefix(s, maxLength).TrimEnd(' ', '.', '-');
        }

        return s;
    }

    /// <summary>Whether <paramref name="value"/> reads as a URL or a domain-style label such as <c>example.com/page</c>.</summary>
    public static bool LooksLikeUrl(string? value)
    {
        var s = CleanWhitespace(value);
        return s.Length > 0 && (IsHttpUrl(s) || DomainLike().IsMatch(PyText.Lower(s)));
    }

    /// <summary>Whether a filename stem still looks like a URL after sanitizing, such as <c>https---example.com-page</c>.</summary>
    public static bool LooksLikeUrlishStem(string? value)
    {
        var s = PyText.Lower(PyText.Strip(CleanWhitespace(value), "-_. "));
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
        s = PyText.Lower(PyText.Strip(RepeatedHyphens().Replace(s, "-"), "-"));
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

    // Python's s.encode("ascii", "ignore").decode("ascii").
    private static string AsciiOnly(string s)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var c in s.Where(char.IsAscii))
        {
            builder.Append(c);
        }

        return builder.ToString();
    }

    [GeneratedRegex($"[{PyText.Space}]+")]
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
