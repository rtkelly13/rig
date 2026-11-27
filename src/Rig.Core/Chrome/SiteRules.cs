using System.Text.RegularExpressions;
using Rig.Core.Text;

namespace Rig.Core.Chrome;

/// <summary>A rule that turns a tab title into an identity key.</summary>
/// <param name="Site">The site the rule covers.</param>
/// <param name="Pattern">An anchored pattern over the stripped title.</param>
/// <param name="Key">A template over the pattern's named groups, as <c>{name}</c>.</param>
/// <param name="Auto">False: a match is only ever reported, never closed.</param>
/// <param name="ExactOnly">True: a duplicate closes only when its title equals the kept tab's exactly.</param>
public sealed record SiteRule(string Site, Regex Pattern, string Key, bool Auto = true, bool ExactOnly = false);

/// <summary>What a tab title names, as a rule keyed it.</summary>
/// <param name="Site">The rule's site.</param>
/// <param name="Key">Tabs with equal keys in one profile show the same page.</param>
/// <param name="Auto">A duplicate may close; false means it is only reported.</param>
/// <param name="ExactOnly">A duplicate closes only when its title equals the kept tab's exactly.</param>
public sealed record TabIdentity(string Site, string Key, bool Auto, bool ExactOnly);

/// <summary>
/// Per-site rules that turn a tab title into an identity key, for de-duplicating non-PR tabs.
/// </summary>
/// <remarks>
/// <para>
/// UI Automation shows a tab's title, never its URL, so two tabs are "the same page" only when a rule says their titles
/// name the same resource. Each rule is an anchored regex over the title (Chrome's suffixes already stripped) and a key
/// template over its named groups; the first matching rule wins.
/// </para>
/// <para>
/// Three safety layers, in order: <see cref="Never"/> titles name no specific resource and are never keyed;
/// <see cref="SiteRule.ExactOnly"/> rules key variant titles together but only close exact copies; and rules with
/// <see cref="SiteRule.Auto"/> false only ever report. GitHub PR pages are not keyed here: the PR planner owns them.
/// </para>
/// </remarks>
public static partial class SiteRules
{
    private const string Dot = "·";
    private const string Repo = @"(?<repo>[\w.-]+/[\w.-]+)";
    private const string GhTail = $"(?: {Dot} GitHub)?";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>Titles that name no specific resource: never keyed, so never closed.</summary>
    public static IReadOnlyList<Regex> Never { get; } = Compile(
        // claude.ai / Claude Code shells: every session, chat and edit view shares these.
        "^Claude$",
        "^Claude Code$",
        "^New (?:chat|task) - Claude$",
        "^Artifact - Claude$",
        // Sign-in and 404 pages on any site.
        @"^(?:Sign in|Log in|Log In|Login|Page not found)\b",
        "Authentication$",
        "^Redirecting",
        // GitHub
        $"^Page not found {Dot} GitHub",
        "^GitHub$",
        "^Sign in to GitHub",
        "^Verify Session$",
        "^Two-factor authentication",
        "^OAuth application authorized$",
        // Google: a Meet tab may be a live call, and meeting names repeat daily.
        "^Google Meet$",
        "^Meet [-–] ",
        "^Google$",
        "^Gmail$",
        "^Google Drive$",
        "^(?:Home|My Drive) - Google Drive$",
        " - Google Search$",
        "^Sign in - Google Accounts$",
        // App shells that title every page the same.
        "^Vercel$",
        "^Slack$",
        "^Ring$",
        "^(?:My )?Dashboard$",
        " - Team Settings - Dashboard$",
        "^Kibana$",
        "^Asana$",
        "^Miro$",
        "^Authorize Application",
        "^Sign in to your account$",
        // AWS: the account is never in the title, and list pages are generic.
        @"^(?:Dashboard|Databases|Console home) \| ",
        " - AWS Developer Tools$",
        "^Amazon Web Services Sign-In$",
        // Rights App: LIVE, clone-N and localhost title their pages identically.
        " - RightsApp Administration$",
        "^Runtime Error$",
        // Locally generated reports share titles across fixture folders.
        "^Datalake schema$",
        "^Schema graph$",
        // Unloaded tabs showing their URL, or nothing.
        "^https?://",
        "^(?:file|chrome|about|edge):",
        @"^\s*$");

    /// <summary>The keying rules, first match wins.</summary>
    public static IReadOnlyList<SiteRule> Rules { get; } =
    [
        Rule("github-issue", $@"^.+ {Dot} Issue #(?<n>\d+) {Dot} {Repo}{GhTail}$", "gh-issue:{repo}#{n}"),
        Rule("github-file", $@"^(?<path>\S+) at (?<ref>\S+) {Dot} {Repo}{GhTail}$", "gh-blob:{repo}@{ref}:{path}"),
        Rule(
            "github-list",
            $"^(?<kind>Pull requests|Issues|Actions|Commits|Releases|Branches) {Dot} {Repo}{GhTail}$",
            "gh-list:{repo}:{kind}"),
        // Re-runs and matrix jobs share a title, so a match is only reported.
        Rule("github-run", $"^(?<t>.+) {Dot} {Repo}@(?<sha>[0-9a-f]{{7,40}})$", "gh-run:{repo}@{sha}:{t}", auto: false),
        Rule("youtrack", @"^.+ : (?<id>[A-Z][A-Z0-9]+-\d+)$", "youtrack:{id}"),
        Rule("jira", @"^\[(?<id>[A-Z][A-Z0-9]+-\d+)\] .+ - (?:JIRA|Jira)$", "jira:{id}"),
        // The unread count changes over time; the account and folder do not.
        Rule("gmail", @"^(?<folder>[^()]+?)(?: \(\d+\))? - (?<acct>\S+@\S+) - .*(?:Gmail|Mail)$", "gmail:{acct}:{folder}"),
        Rule("google-calendar", "^(?<acct>.+?) – Calendar - (?<view>.+)$", "gcal:{acct}:{view}"),
        Rule("google-docs", "^(?<t>.+) - Google (?<kind>Docs|Sheets|Slides|Forms)$", "gdoc:{kind}:{t}"),
        Rule(
            "google-drive",
            "^(?!(?:Home|My Drive|Search results|Shared with me|Recent) - )(?<t>.+) - Google Drive$",
            "gdrive:{t}"),
        // A leading dot is Asana's unread marker.
        Rule("asana", "^(?:● )?(?!(?:Home|Log in|My tasks|Inbox) - )(?<t>.+) - Asana$", "asana:{t}"),
        Rule(
            "aws",
            @"^(?<res>.+?) - (?<page>[^|]+Details) \| (?<svc>[^|]+) \| (?<region>[a-z]{2}-[a-z]+-\d)$",
            "aws:{region}:{svc}:{res}"),
        Rule("nuget", @"^NuGet Gallery \| (?<t>(?!Home$|Sign In$).+)$", "nuget:{t}"),
        // claude.ai loads one artifact titled either `X` or `X - Claude`; key both as the bare title.
        Rule("claude", "^(?<t>.+) - Claude$", "title:{t}", exactOnly: true),
        // Anything else: the exact title. A one-word title names too little to trust.
        Rule("title", @"^(?<t>\S+(?:\s+\S+)+)$", "title:{t}"),
        Rule("title", "^(?<t>.+)$", "title:{t}", auto: false),
    ];

    /// <summary>
    /// Video-call pages, kept by every rule even when muted with the camera off, which Chrome's alerts cannot show.
    /// </summary>
    public static IReadOnlyList<Regex> Calls { get; } = Compile(
        "^Meet [-–] ",
        "^(?:Google )?Meet$",
        @"^Zoom(?: Meeting| Workplace| Webinar)?\b",
        @"\| Microsoft Teams$",
        @"^(?:Huddle|Call)\b.* - Slack$",
        @"\bWebex\b",
        @"^Jitsi Meet\b",
        @" \| Whereby$");

    /// <summary>The title is a video call.</summary>
    public static bool IsCall(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var text = UnicodeText.Strip(title);
        return Calls.Any(pattern => pattern.IsMatch(text));
    }

    /// <summary>
    /// The address is a meeting itself, not a call site's lobby, home or chat page: a Meet code, a Zoom web client or
    /// join link, a Teams meeting join, a Slack huddle, a Webex meeting, or a named Jitsi or Whereby room. For a tab whose
    /// audio and alerts are unknown, this is the call evidence; a call site's title alone is not.
    /// </summary>
    public static bool IsCallRoom(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            return false;
        }

        var host = url.Host.ToLowerInvariant();
        var path = url.AbsolutePath.ToLowerInvariant();
        var first = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return host switch
        {
            "meet.google.com" => MeetCode().IsMatch(first),
            "meet.jit.si" => first.Length > 0,
            _ when OnHost(host, "whereby.com") => first.Length > 0,
            "app.slack.com" => path.Contains("/huddle/", StringComparison.Ordinal),
            "teams.microsoft.com" or "teams.live.com" => path.Contains("meetup-join", StringComparison.Ordinal)
                || url.Query.Contains("meetingjoin", StringComparison.OrdinalIgnoreCase),
            _ when OnHost(host, "zoom.us") => first is "wc" or "j" or "s",
            _ when OnHost(host, "webex.com") => first is "meet" or "wbxmjs" or "webappng",
            _ => false,
        };
    }

    /// <summary>The title is a call or names nothing specific, so it is never de-duplicated.</summary>
    public static bool NeverDedupe(string title) => IsCall(title) || Never.Any(pattern => pattern.IsMatch(title));

    /// <summary>The identity key a tab title names, or null when it names nothing trustworthy.</summary>
    public static TabIdentity? Identify(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var text = UnicodeText.Strip(title);
        if (NeverDedupe(text))
        {
            return null;
        }

        foreach (var rule in Rules)
        {
            var match = rule.Pattern.Match(text);
            if (match.Success)
            {
                var key = Placeholder().Replace(rule.Key, slot => UnicodeText.Strip(match.Groups[slot.Groups[1].Value].Value));
                return new TabIdentity(rule.Site, key, rule.Auto, rule.ExactOnly);
            }
        }

        return null;
    }

    private static Regex[] Compile(params string[] patterns) =>
        [.. patterns.Select(pattern => new Regex(pattern, RegexOptions.CultureInvariant, Timeout))];

    private static SiteRule Rule(string site, string pattern, string key, bool auto = true, bool exactOnly = false) =>
        new(site, new Regex(pattern, RegexOptions.CultureInvariant, Timeout), key, auto, exactOnly);

    private static bool OnHost(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[a-z]{3}-[a-z]{4}-[a-z]{3}$")]
    private static partial Regex MeetCode();
}
