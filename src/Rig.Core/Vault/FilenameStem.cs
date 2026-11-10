using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>
/// Suggests a clean filename stem for a note from its current stem, title, source URL and related article, ported from
/// <c>suggest_clean_filename_stem</c>. Each source gives a candidate, the candidates are scored, and the best one that does
/// not look like a URL wins, the earliest on a tie.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054", Justification = "URLs are kept as written; System.Uri normalizes them.")]
public static partial class FilenameStem
{
    private const string SocialSuffix = "-social";

    private static readonly HashSet<string> Tlds = new(StringComparer.Ordinal)
    {
        "com", "org", "net", "io", "dev", "ai", "app", "co", "uk", "eu", "fm", "me", "tv", "ly", "sh", "rs",
    };

    private static readonly HashSet<string> GenericSlugs = new(StringComparer.Ordinal)
    {
        "app", "apps", "article", "articles", "blog", "box", "default", "docs", "document", "edit", "forum", "google", "home",
        "index", "new", "post", "posts", "resource", "showpost", "showthread", "spreadsheets", "thread", "video", "watch",
    };

    private static readonly Dictionary<string, HashSet<string>> HostSpecificSlugs = new(StringComparer.Ordinal)
    {
        ["docs.google.com"] = new(StringComparer.Ordinal) { "edit", "view", "preview", "copy", "export", "spreadsheets", "document" },
        ["docs.googleusercontent.com"] = new(StringComparer.Ordinal) { "edit", "view", "preview" },
        ["youtube.com"] = new(StringComparer.Ordinal) { "watch", "channel", "playlist", "live" },
        ["youtu.be"] = new(StringComparer.Ordinal) { "watch" },
        ["box.com"] = new(StringComparer.Ordinal) { "s", "file", "folder" },
        ["app.box.com"] = new(StringComparer.Ordinal) { "s", "file", "folder" },
        ["xda-developers.com"] = new(StringComparer.Ordinal) { "showpost", "showthread", "forums", "forum" },
        ["forum.xda-developers.com"] = new(StringComparer.Ordinal) { "showpost", "showthread", "forums", "forum" },
        ["diagrams.net"] = new(StringComparer.Ordinal) { "app" },
        ["app.diagrams.net"] = new(StringComparer.Ordinal) { "app" },
    };

    // Applied in this order, so ".net" is rewritten before "asp.net" is looked for.
    private static readonly (string Old, string New)[] StemTerms =
    [
        (".net", " dotnet "), ("asp.net", " asp dotnet "), ("node.js", " nodejs "), ("next.js", " nextjs "),
        ("gatsby.js", " gatsbyjs "), ("knockout.js", " knockoutjs "), ("moment.js", " momentjs "), ("draw.io", " draw io "),
        ("json-generator.com", " json generator "), ("plex.tv", " plex "), ("codeproject", " code project "),
        ("stack overflow", " stack overflow "), ("youtube", " youtube "),
    ];

    private static readonly HashSet<string> IndexSegments = new(StringComparer.Ordinal) { "index", "home", "default", "index.html" };

    private readonly record struct Candidate(string Stem, bool FromUrl);

    /// <summary>
    /// A cleaner stem for <paramref name="currentStem"/>, keeping a <c>-social</c> suffix and a four-hex-digit collision
    /// suffix such as <c>-a1b2</c>.
    /// </summary>
    /// <exception cref="FormatException"><paramref name="sourceUrl"/> has a malformed netloc (see <see cref="PyUrl.Split"/>).</exception>
    public static string Suggest(string? currentStem, string title = "", string sourceUrl = "", string relatedArticle = "", int maxLength = 80)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(sourceUrl);
        var stem = PyText.Strip(currentStem ?? string.Empty);
        var social = string.Empty;
        if (PyText.Lower(stem).EndsWith(SocialSuffix, StringComparison.Ordinal))
        {
            social = SocialSuffix;
            stem = stem[..^SocialSuffix.Length];
        }

        var collision = string.Empty;
        var match = CollisionSuffix().Match(stem);
        if (match.Success)
        {
            collision = "-" + match.Groups[1].Value;
            stem = stem[..^collision.Length];
        }

        var candidates = new List<Candidate>();
        var related = RelatedArticleStem(relatedArticle);
        if (social.Length > 0 && related.Length > 0)
        {
            candidates.Add(new(related, FromUrl: false));
        }

        var cleanTitle = VaultText.CleanWhitespace(title);
        if (cleanTitle.Length > 0)
        {
            cleanTitle = PyText.Strip(SocialDraftLead().Replace(cleanTitle, string.Empty, 1));
            cleanTitle = StripSourceHostPrefix(cleanTitle, sourceUrl);
            cleanTitle = StripHostBrandSuffix(cleanTitle, sourceUrl);
            cleanTitle = ExpandCommonTerms(cleanTitle);
            cleanTitle = RemoveDomainLikeLead(cleanTitle);
            candidates.Add(new(cleanTitle, FromUrl: false));
        }

        if (VaultText.LooksLikeUrlishStem(stem))
        {
            candidates.Add(new(StemFromUrlishStem(stem), FromUrl: true));
        }

        if (sourceUrl.Length > 0)
        {
            candidates.Add(new(StemFromUrl(sourceUrl), FromUrl: true));
        }

        var normalized = VaultText.CleanWhitespace(ExpandCommonTerms(stem).Replace('.', ' '));
        if (normalized.Length > 0)
        {
            normalized = RemoveDomainLikeLead(StripHostBrandSuffix(normalized, sourceUrl));
            candidates.Add(new(normalized, FromUrl: false));
        }

        var viable = new List<(string Stem, int Quality)>();
        var fallback = new List<(string Stem, int Quality)>();
        foreach (var candidate in candidates.Where(c => c.Stem.Length > 0))
        {
            var scored = (candidate.Stem, Quality(candidate.Stem, sourceUrl, candidate.FromUrl));
            (VaultText.LooksLikeUrl(candidate.Stem) || VaultText.LooksLikeUrlishStem(candidate.Stem) ? fallback : viable).Add(scored);
        }

        var chosen = viable.Count > 0 ? viable : fallback;
        var baseStem = chosen.Count > 0 ? chosen.MaxBy(item => item.Quality).Stem : "untitled";
        var suggested = VaultText.SlugifyNoteStem(baseStem, maxLength);
        return VaultText.SanitizeFilename(suggested + collision + social, maxLength + collision.Length + social.Length);
    }

    private static string RelatedArticleStem(string? value)
    {
        var match = WikilinkTarget().Match(value ?? string.Empty);
        if (!match.Success)
        {
            return string.Empty;
        }

        var raw = match.Groups[1].Value.Split('/')[^1];
        return PyText.Strip(raw.EndsWith(".md", StringComparison.Ordinal) ? raw[..^3] : raw);
    }

    private static string Host(string url)
    {
        var host = PyText.Lower(PyUrl.Split(url).Hostname);
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static string StemFromUrl(string url)
    {
        var cleaned = VaultText.CleanWhitespace(url);
        if (cleaned.Length == 0)
        {
            return "untitled";
        }

        var split = PyUrl.Split(cleaned);
        var host = PyText.Lower(split.Hostname);
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        var parts = split.Path.Split('/')
            .Where(part => part.Length > 0)
            .Select(part => FileExtension().Replace(PyUrl.Unquote(part), string.Empty))
            .ToList();
        if (host is "x.com" or "twitter.com")
        {
            if (parts.Count >= 3 && string.Equals(PyText.Lower(parts[1]), "status", StringComparison.Ordinal))
            {
                return VaultText.SlugifyNoteStem($"{parts[0]} {parts[2]}");
            }

            if (parts.Count > 0 && !string.Equals(PyText.Lower(parts[0]), "i", StringComparison.Ordinal))
            {
                return VaultText.SlugifyNoteStem(parts[0]);
            }
        }

        foreach (var part in Enumerable.Reverse(parts))
        {
            if (!IndexSegments.Contains(PyText.Lower(part)))
            {
                return VaultText.SlugifyNoteStem(part);
            }
        }

        var label = host.Length > 0 ? host.Split('.')[0] : string.Empty;
        return VaultText.SlugifyNoteStem(label.Length > 0 ? label : cleaned);
    }

    private static bool IsBadSlug(string slug, string sourceUrl)
    {
        var candidate = VaultText.SlugifyNoteStem(slug);
        if (candidate is "untitled" || GenericSlugs.Contains(candidate))
        {
            return true;
        }

        if ((candidate.Length <= 3 && !AnyDigit().IsMatch(candidate)) || LongToken().IsMatch(candidate))
        {
            return true;
        }

        return HostSpecificSlugs.TryGetValue(Host(sourceUrl), out var slugs) && slugs.Contains(candidate);
    }

    private static int Quality(string slug, string sourceUrl, bool fromUrl)
    {
        var candidate = VaultText.SlugifyNoteStem(slug);
        if (candidate is "untitled")
        {
            return -100;
        }

        var score = Math.Min(candidate.Length, 60) + (candidate.Count(c => c == '-') * 2);
        score += FourLetters().IsMatch(candidate) ? 10 : 0;
        score += AnyDigit().IsMatch(candidate) ? 2 : 0;
        score -= IsBadSlug(candidate, sourceUrl) ? 80 : 0;
        score -= fromUrl ? 8 : 0;
        return score;
    }

    private static string StripSourceHostPrefix(string title, string sourceUrl)
    {
        var cleaned = VaultText.CleanWhitespace(title);
        if (cleaned.Length == 0 || sourceUrl.Length == 0)
        {
            return cleaned;
        }

        var host = Host(sourceUrl);
        if (host.Length == 0)
        {
            return cleaned;
        }

        var prefixes = new List<string> { host };
        var firstLabel = host.Split('.')[0];
        if (firstLabel.Length > 0 && !string.Equals(firstLabel, host, StringComparison.Ordinal))
        {
            prefixes.Add(firstLabel);
        }

        foreach (var prefix in prefixes)
        {
            var pattern = new Regex($"^{Regex.Escape(prefix)}[.\\-:{PyText.Space}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            var updated = PyText.Strip(pattern.Replace(cleaned, string.Empty));
            if (!string.Equals(updated, cleaned, StringComparison.Ordinal))
            {
                return updated;
            }
        }

        return cleaned;
    }

    private static string StripHostBrandSuffix(string title, string sourceUrl)
    {
        var cleaned = VaultText.CleanWhitespace(title);
        if (cleaned.Length == 0 || sourceUrl.Length == 0)
        {
            return cleaned;
        }

        var host = Host(sourceUrl);
        if (host.Length == 0)
        {
            return cleaned;
        }

        var labels = host.Split('.');
        var suffixes = new List<string> { host, labels[0] };
        suffixes.AddRange(labels.Where(label => !Tlds.Contains(label)));
        var updated = cleaned;
        foreach (var suffix in suffixes.Select(PyText.Strip).Where(suffix => suffix.Length > 0))
        {
            var pattern = new Regex($"[{PyText.Space}\\-:|()]+{Regex.Escape(suffix)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            var candidate = PyText.Strip(pattern.Replace(updated, string.Empty));
            if (candidate.Length > 0 && !string.Equals(candidate, updated, StringComparison.Ordinal))
            {
                updated = candidate;
            }
        }

        return updated;
    }

    private static string ExpandCommonTerms(string text)
    {
        var lowered = PyText.Lower(VaultText.CleanWhitespace(text));
        foreach (var (old, replacement) in StemTerms)
        {
            lowered = lowered.Replace(old, replacement, StringComparison.Ordinal);
        }

        return VaultText.CleanWhitespace(lowered);
    }

    private static string RemoveDomainLikeLead(string value)
    {
        var updated = PyText.Strip(DomainLead().Replace(VaultText.CleanWhitespace(value), string.Empty));
        return PyText.Strip(SpacedDomainLead().Replace(updated, string.Empty));
    }

    private static string StemFromUrlishStem(string stem)
    {
        var normalized = PyText.Strip(VaultText.CleanWhitespace(stem), "-_. ");
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var social = string.Empty;
        if (PyText.Lower(normalized).EndsWith(SocialSuffix, StringComparison.Ordinal))
        {
            social = SocialSuffix;
            normalized = normalized[..^SocialSuffix.Length].TrimEnd('-', '_', '.', ' ');
        }

        var tokenized = PyText.Lower(PyText.Strip(DotsAndUnderscores().Replace(normalized, "-"), "-"));
        if (tokenized.Length == 0 || !VaultText.LooksLikeUrlishStem(normalized))
        {
            return string.Empty;
        }

        var parts = Hyphens().Split(tokenized).Where(part => part.Length > 0).ToList();
        if (parts.Count > 0 && parts[0] is "http" or "https")
        {
            parts.RemoveAt(0);
        }

        if (parts.Count > 0 && parts[0] is "www")
        {
            parts.RemoveAt(0);
        }

        var hostEnd = parts.FindIndex(Tlds.Contains);
        if (hostEnd < 0)
        {
            return string.Empty;
        }

        if (hostEnd + 1 < parts.Count && Tlds.Contains(parts[hostEnd + 1]))
        {
            hostEnd++;
        }

        var rest = parts.Skip(hostEnd + 1).ToList();
        return (rest.Count > 0 ? VaultText.SlugifyNoteStem(string.Join('-', rest)) : VaultText.SlugifyNoteStem(parts[0])) + social;
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    [GeneratedRegex("-([a-f0-9]{4})$")]
    private static partial Regex CollisionSuffix();

    [GeneratedRegex($"^social draft for[{PyText.Space}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SocialDraftLead();

    [GeneratedRegex(@"\[\[([^\]|]+)")]
    private static partial Regex WikilinkTarget();

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,8}$")]
    private static partial Regex FileExtension();

    [GeneratedRegex(@"\d")]
    private static partial Regex AnyDigit();

    [GeneratedRegex(@"\A[a-z0-9]{10,}\z")]
    private static partial Regex LongToken();

    [GeneratedRegex("[a-z]{4,}")]
    private static partial Regex FourLetters();

    [GeneratedRegex($@"^(?:[a-z0-9-]+(?:\.[a-z0-9-]+)+)[.\-:{PyText.Space}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DomainLead();

    [GeneratedRegex($@"^(?:[a-z0-9-]+[{PyText.Space}]+)+(?:com|org|net|io|dev|ai|app|co|uk|eu|fm|me|tv|ly|sh|rs)[.\-:{PyText.Space}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpacedDomainLead();

    [GeneratedRegex("[_.]+")]
    private static partial Regex DotsAndUnderscores();

    [GeneratedRegex("-+")]
    private static partial Regex Hyphens();
}
