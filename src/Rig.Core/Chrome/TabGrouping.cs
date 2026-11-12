using System.Globalization;
using System.Text.RegularExpressions;
using Rig.Core.GitHub;
using Rig.Core.Workspace;

namespace Rig.Core.Chrome;

/// <summary>One tab to move into its key's group.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="WindowNumber">1-based number of the tab's window, in the order the windows were given.</param>
/// <param name="Profile">The profile's display name.</param>
/// <param name="Key">The repo (<c>owner/repo</c>) or site the tab belongs to.</param>
/// <param name="Title">The group's title.</param>
/// <param name="New">
/// The tab that creates the group; every later move joins it. A creating tab from another window first moves to the
/// home window, then creates it there.
/// </param>
/// <param name="HomeHandle">The window the group lives in; a tab from another window moves there.</param>
/// <param name="HomeNumber">That window's number.</param>
public sealed record GroupMove(ChromeTab Tab, int WindowNumber, string Profile, string Key, string Title, bool New, long HomeHandle, int HomeNumber)
{
    /// <summary>The tab moves to another window.</summary>
    public bool Crosses => Tab.WindowHandle != HomeHandle;
}

/// <summary>A duplicate group to dissolve, so its tabs can join the key's home group.</summary>
/// <param name="Group">The group.</param>
/// <param name="Tabs">Its tabs.</param>
/// <param name="WindowNumber">Its window's number.</param>
/// <param name="Profile">The profile's display name.</param>
public sealed record Ungroup(TabGroup Group, IReadOnlyList<ChromeTab> Tabs, int WindowNumber, string Profile);

/// <summary>A window grouping leaves alone.</summary>
/// <param name="WindowNumber">Its number.</param>
/// <param name="Profile">The profile's display name.</param>
/// <param name="Reason">Why.</param>
public sealed record SkippedWindow(int WindowNumber, string Profile, string Reason);

/// <summary>A key grouping leaves alone.</summary>
/// <param name="Profile">The profile's display name.</param>
/// <param name="Title">The group title.</param>
/// <param name="Reason">Why.</param>
public sealed record BlockedKey(string Profile, string Title, string Reason);

/// <summary>What grouping will do.</summary>
/// <param name="Moves">In execution order: window by window.</param>
/// <param name="Skipped">Windows left alone.</param>
/// <param name="AlreadyGrouped">Tabs already in the group they would go to.</param>
/// <param name="Elsewhere">Tabs left alone because they sit in some other group, by hand.</param>
/// <param name="Ungroups">Duplicates to dissolve, before any move.</param>
/// <param name="Blocked">Keys left alone.</param>
public sealed record GroupPlan(
    IReadOnlyList<GroupMove> Moves,
    IReadOnlyList<SkippedWindow> Skipped,
    int AlreadyGrouped,
    int Elsewhere,
    IReadOnlyList<Ungroup> Ungroups,
    IReadOnlyList<BlockedKey> Blocked)
{
    /// <summary>The windows whose tabs move, in the order to work through them.</summary>
    public IReadOnlyList<int> WindowNumbers() => [.. Moves.Select(move => move.WindowNumber).Distinct()];

    /// <summary>
    /// The windows with duplicates to dissolve. Every ungroup runs before any move, so each join finds exactly one group
    /// with its title.
    /// </summary>
    public IReadOnlyList<int> UngroupWindowNumbers() => [.. Ungroups.Select(ungroup => ungroup.WindowNumber).Distinct()];
}

/// <summary>How to plan grouping.</summary>
/// <param name="MinSize">A key with no group needs this many loose tabs to start one.</param>
/// <param name="AllDesktops">Gather windows on every virtual desktop; false skips windows off the current one.</param>
/// <param name="Hosts">Per profile key, the host history ties each title to (see <see cref="ChromeHistory"/>).</param>
/// <param name="Owners">Ties local and Vercel URLs to the projects that declare them.</param>
/// <param name="Sites">Hosts to site groups; null for the copy built into rig.</param>
public sealed record GroupOptions(
    int MinSize = 2,
    bool AllDesktops = true,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Hosts = null,
    UrlOwners? Owners = null,
    SiteHosts? Sites = null);

/// <summary>
/// Decides which tabs to gather into one tab group per GitHub repository or work site. Ported from
/// <c>desktop_manager.chrome.grouping</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Plan"/> is a pure function of the Chrome windows. Chrome's "Add tab to group" menu lists every group in
/// the profile by title alone, whichever window it is in, and picking one moves the tab into that group's window. So
/// the plan works per profile, not per window:
/// </para>
/// <list type="number">
/// <item><b>One group per key per profile</b>: a key is a repo (titled with the repo name, or <c>owner/repo</c> when two
/// repos in the profile share a name) or a site (<c>Google</c>, <c>Asana</c>, <c>YouTrack</c>, <c>Claude</c>, …).</item>
/// <item><b>One home window</b>: every repo group lives in the profile's review window, the one holding the most GitHub
/// tabs (the earlier on a tie). A site's home is its existing group's window, or else the window holding most of its
/// tabs.</item>
/// <item><b>Duplicates merge</b>: every group with the key's title but the home one is ungrouped first and its tabs
/// join the home group. A duplicate that also holds other keys' tabs was grouped by hand, and blocks the key.</item>
/// <item><b>A new group needs company</b>: a key with no group and fewer than <c>MinSize</c> loose tabs stays put.</item>
/// <item><b>Hands off</b>: pinned tabs, call tabs and tabs in a group with another title never move.</item>
/// <item><b>Every desktop</b> by default; otherwise windows on other desktops are skipped, and a key with a group on
/// another desktop is blocked.</item>
/// </list>
/// <para>
/// A tab's repo comes from its title as GitHub writes it, else its URL (a github.com page, or a local or Vercel URL a
/// project declares). Its site comes from the title shapes <see cref="SiteRules"/> knows and the sites' own shells, and
/// failing those its host: its URL's, else the one history ties its title to.
/// </para>
/// </remarks>
public static partial class TabGrouping
{
    private const string Name = @"[\w.-]+";

    private static readonly Lazy<SiteHosts> PackagedSites = new(SiteHosts.Packaged);

    private static readonly Dictionary<string, string> SiteOfRule = new(StringComparer.Ordinal)
    {
        ["gmail"] = "Google",
        ["google-calendar"] = "Google",
        ["google-docs"] = "Google",
        ["google-drive"] = "Google",
        ["asana"] = "Asana",
        ["youtrack"] = "YouTrack",
    };

    /// <summary>Titles <see cref="SiteRules.Identify"/> leaves unkeyed (app shells, lists, searches) that still belong to a site.</summary>
    private static readonly (string Site, Regex Pattern)[] SiteTitles =
    [
        ("Google", new(@" - Google (?:Search|Docs|Sheets|Slides|Forms|Drive)$| - (?:Gmail|Google Calendar)$", RegexOptions.CultureInvariant)),
        ("AI", new(@"(?:^| - )(?:Google )?Gemini$|^ChatGPT$", RegexOptions.CultureInvariant)),
        ("Asana", new(@"(?:^| - )Asana$", RegexOptions.CultureInvariant)),
        ("YouTrack", new(@"\bYouTrack\b", RegexOptions.CultureInvariant)),
        ("Claude", new(@"(?:^| - )Claude(?: Code)?$", RegexOptions.CultureInvariant)),
    ];

    /// <summary>Hosts whose tabs may be a live call, whatever their title.</summary>
    private static readonly string[] CallHosts = ["meet.google.com", "zoom.us", "teams.microsoft.com", "teams.live.com", "whereby.com", "meet.jit.si"];

    /// <summary>First path segments of github.com pages that are not <c>owner/repo</c>.</summary>
    private static readonly HashSet<string> GitHubNonOwners = new(
        [
            "orgs", "users", "search", "settings", "notifications", "pulls", "issues", "marketplace", "sponsors", "topics",
            "login", "new", "organizations", "apps", "features", "explore", "codespaces", "collections", "trending", "about",
            "enterprise", "dashboard", "account",
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> NoHosts = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The GitHub repository a tab's title names, or null for any other page: <c>… · owner/repo</c> (optionally
    /// <c>@sha</c>, or a trailing <c>· GitHub</c>), <c>owner/repo: description</c> or <c>GitHub - owner/repo…</c>.
    /// </summary>
    public static RepoSlug? RepoOf(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var match = TrailingRepo().Match(title);
        if (!match.Success)
        {
            match = HomeRepo().Match(title);
        }

        return match.Success ? new RepoSlug(match.Groups["owner"].Value, match.Groups["repo"].Value) : null;
    }

    /// <summary>The work site a tab's title (or, failing that, its <paramref name="host"/>) belongs to, as its group title, or null. Calls never group.</summary>
    public static string? SiteOf(string title, string? host = null, SiteHosts? sites = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        var text = title.Trim();
        if (SiteRules.IsCall(text))
        {
            return null;
        }

        if (SiteRules.Identify(text) is { } identity && SiteOfRule.TryGetValue(identity.Site, out var ruled))
        {
            return ruled;
        }

        foreach (var (site, pattern) in SiteTitles)
        {
            if (pattern.IsMatch(text))
            {
                return site;
            }
        }

        return host is null ? null : SiteOfHost(host, sites);
    }

    /// <summary>The site group a host belongs to, from <c>config/chrome-site-groups.json</c>; calls never group.</summary>
    public static string? SiteOfHost(string host, SiteHosts? sites = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.ToLowerInvariant();
        if (CallHosts.Any(call => name == call || name.EndsWith("." + call, StringComparison.Ordinal)))
        {
            return null;
        }

        return (sites ?? PackagedSites.Value).SiteFor(name);
    }

    /// <summary>The repo a tab's URL names: a GitHub repo page, or a local or Vercel URL a project declares.</summary>
    public static RepoSlug? RepoOfUrl(Uri? url, UrlOwners? owners = null)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return null;
        }

        if (url.Host.ToLowerInvariant() is "github.com" or "www.github.com")
        {
            var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2 && !GitHubNonOwners.Contains(segments[0].ToLowerInvariant())
                ? new RepoSlug(segments[0], segments[1])
                : null;
        }

        return (owners ?? UrlOwners.None).SlugFor(url) is { } slug ? RepoSlug.FromPath(slug) : null;
    }

    /// <summary>The repo or site a tab groups under, from its title first and then its URL.</summary>
    public static string? TabKey(ChromeTab tab, IReadOnlyDictionary<string, string>? hosts = null, UrlOwners? owners = null, SiteHosts? sites = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if ((RepoOf(tab.Title) ?? RepoOfUrl(tab.Url, owners)) is { } repo)
        {
            return repo.FullName;
        }

        var host = tab.Url is { IsAbsoluteUri: true } url ? url.Host.ToLowerInvariant() : (hosts ?? NoHosts).GetValueOrDefault(tab.Title);
        return SiteOf(tab.Title, host, sites);
    }

    /// <summary>The repo (<c>owner/repo</c>) or site a tab groups under, or null.</summary>
    public static string? KeyOf(string title, string? host = null, SiteHosts? sites = null) =>
        RepoOf(title)?.FullName ?? SiteOf(title, host, sites);

    /// <summary>
    /// The grouping plan. Pass the windows in the order to work through them (screen order); ties go to the earlier.
    /// </summary>
    public static GroupPlan Plan(IReadOnlyList<ChromeWindow> windows, int? currentDesktop, GroupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        options ??= new GroupOptions();
        var context = new Context(options.MinSize, options.Owners ?? UrlOwners.None, options.Sites ?? PackagedSites.Value);
        List<GroupMove> moves = [];
        List<Ungroup> ungroups = [];
        List<SkippedWindow> skipped = [];
        List<BlockedKey> blocked = [];
        int already = 0, elsewhere = 0;

        var numbered = windows.Select((window, index) => new Numbered(index + 1, window));
        foreach (var profile in numbered.GroupBy(item => item.Window.ProfileKey, StringComparer.Ordinal))
        {
            HashSet<long> eligible = [];
            foreach (var (number, window) in profile)
            {
                if (!options.AllDesktops && currentDesktop is not null && window.DesktopNumber != currentDesktop)
                {
                    var where = window.DesktopNumber is { } desktop
                        ? string.Create(CultureInfo.InvariantCulture, $"desktop {desktop}")
                        : "an unknown desktop";
                    skipped.Add(new(number, ProfileOf(window), $"on {where}, not the current one"));
                }
                else
                {
                    eligible.Add(window.Handle);
                }
            }

            var hosts = options.Hosts?.GetValueOrDefault(profile.Key) ?? NoHosts;
            var result = PlanProfile([.. profile], eligible, hosts, context);
            moves.AddRange(result.Moves);
            ungroups.AddRange(result.Ungroups);
            blocked.AddRange(result.Blocked);
            already += result.AlreadyGrouped;
            elsewhere += result.Elsewhere;
        }

        var order = ExecutionOrder(moves);
        return new GroupPlan(
            [.. moves.OrderBy(move => order[move.WindowNumber]).ThenBy(Phase).ThenBy(move => move.Tab.Index)],
            [.. skipped.OrderBy(skip => skip.WindowNumber)],
            already,
            elsewhere,
            [.. ungroups.OrderBy(ungroup => ungroup.WindowNumber)],
            blocked);
    }

    private static string ProfileOf(ChromeWindow window) =>
        string.IsNullOrEmpty(window.ProfileName) ? window.ProfileKey : window.ProfileName;

    private static GroupPlan PlanProfile(IReadOnlyList<Numbered> windows, HashSet<long> eligible, IReadOnlyDictionary<string, string> hosts, Context context)
    {
        var keys = Keys(windows, hosts, context);
        var groups = windows
            .SelectMany(item => item.Window.Tabs.Where(tab => tab.Group is not null).Select(tab => new Found(item.Window, item.Number, tab)))
            .GroupBy(found => found.Tab.Group!.Title, StringComparer.Ordinal)
            .ToDictionary(
                byTitle => byTitle.Key,
                byTitle => byTitle.GroupBy(found => found.Tab.Group!.RuntimeId).Select(members => members.ToList()).ToList(),
                StringComparer.Ordinal);
        var review = ReviewWindow(keys, eligible);

        List<GroupMove> moves = [];
        List<Ungroup> ungroups = [];
        List<BlockedKey> blocked = [];
        int already = 0, elsewhere = 0;
        foreach (var spec in keys)
        {
            var live = spec.Tabs.Where(found => eligible.Contains(found.Window.Handle)).ToList();
            var loose = live.Where(found => found.Tab.Group is null).ToList();
            elsewhere += live.Count(found => found.Tab.Group is { } group && group.Title != spec.Title);
            var everyGroup = groups.GetValueOrDefault(spec.Title) ?? [];
            var titled = everyGroup.Where(members => eligible.Contains(members[0].Window.Handle)).ToList();
            if (titled.Count == 0 && loose.Count == 0)
            {
                continue;
            }

            var profile = ProfileOf(spec.Tabs[0].Window);
            if (titled.Count < everyGroup.Count)
            {
                // Some group with its title is away; with none here there are loose tabs here, so either way it blocks.
                var away = everyGroup.Select(DesktopOf).Except(titled.Select(DesktopOf)).Order();
                blocked.Add(new(profile, spec.Title, $"its group is on desktop {string.Join(", ", away)}"));

                already += titled.Sum(members => members.Count);
                continue;
            }

            if (titled.Count == 0 && loose.Count < context.MinSize)
            {
                continue;
            }

            var ranked = titled.OrderByDescending(members => members.Count).ThenBy(members => members[0].Number).ToList();
            var target = spec.Repo ? review : null;
            var home = target is not null
                ? ranked.FirstOrDefault(members => members[0].Window.Handle == target.Window.Handle)
                : ranked.FirstOrDefault();
            var duplicates = ranked.Where(members => !ReferenceEquals(members, home)).ToList();
            if (duplicates.SelectMany(members => members).Any(found => TabKey(found.Tab, hosts, context.Owners, context.Sites) != spec.Key))
            {
                blocked.Add(new(profile, spec.Title, string.Create(CultureInfo.InvariantCulture, $"{titled.Count} groups share its title, one holds other tabs")));
                already += titled.Sum(members => members.Count);
                continue;
            }

            already += home?.Count ?? 0;
            var joiners = loose.Concat(duplicates.SelectMany(members => members))
                .OrderBy(found => found.Number)
                .ThenBy(found => found.Tab.Index)
                .ToList();
            ungroups.AddRange(duplicates.Select(members => new Ungroup(members[0].Tab.Group!, [.. members.Select(found => found.Tab)], members[0].Number, profile)));
            if (home is not null)
            {
                moves.AddRange(joiners.Select(found => Move(found, spec, profile, home[0], isNew: false)));
                continue;
            }

            if (target is null)
            {
                var perWindow = joiners.GroupBy(found => found.Number).ToList();
                target = perWindow.OrderByDescending(window => window.Count()).ThenBy(window => window.Key).First().First();
            }

            var creator = joiners.FirstOrDefault(found => found.Window.Handle == target.Window.Handle) ?? joiners[0];
            moves.Add(Move(creator, spec, profile, target, isNew: true));
            moves.AddRange(joiners.Where(found => !ReferenceEquals(found, creator)).Select(found => Move(found, spec, profile, target, isNew: false)));
        }

        return new GroupPlan(moves, [], already, elsewhere, ungroups, blocked);
    }

    private static int DesktopOf(List<Found> members) => members[0].Window.DesktopNumber ?? 0;

    /// <summary>The window holding the most GitHub tabs, where every repo group lives.</summary>
    private static Found? ReviewWindow(IEnumerable<KeySpec> keys, HashSet<long> eligible) =>
        keys.Where(spec => spec.Repo)
            .SelectMany(spec => spec.Tabs)
            .Where(found => eligible.Contains(found.Window.Handle))
            .GroupBy(found => found.Number)
            .OrderByDescending(window => window.Count())
            .ThenBy(window => window.Key)
            .FirstOrDefault()?
            .First();

    /// <summary>Every repo and site among the profile's movable tabs, titled so no two share a title.</summary>
    private static List<KeySpec> Keys(IReadOnlyList<Numbered> windows, IReadOnlyDictionary<string, string> hosts, Context context)
    {
        Dictionary<string, RepoSlug> repos = new(StringComparer.Ordinal);
        Dictionary<string, KeySpec> byKey = new(StringComparer.Ordinal);
        List<KeySpec> keys = [];
        foreach (var (number, window) in windows)
        {
            foreach (var tab in window.Tabs.Where(tab => !tab.Pinned))
            {
                var repo = RepoOf(tab.Title) ?? RepoOfUrl(tab.Url, context.Owners);
                if (TabKey(tab, hosts, context.Owners, context.Sites) is not { } key)
                {
                    continue;
                }

                if (repo is not null)
                {
                    repos[key] = repo;
                }

                if (!byKey.TryGetValue(key, out var spec))
                {
                    spec = new KeySpec(key, repo is not null) { Title = key };
                    byKey[key] = spec;
                    keys.Add(spec);
                }

                spec.Tabs.Add(new(window, number, tab));
            }
        }

        var names = repos.Values.CountBy(repo => repo.Name.ToUpperInvariant()).ToDictionary(StringComparer.Ordinal);
        foreach (var (key, repo) in repos)
        {
            if (names[repo.Name.ToUpperInvariant()] == 1)
            {
                byKey[key].Title = repo.Name;
            }
        }

        return keys;
    }

    private static GroupMove Move(Found found, KeySpec spec, string profile, Found home, bool isNew) =>
        new(found.Tab, found.Number, profile, spec.Key, spec.Title, isNew, home.Window.Handle, home.Number);

    /// <summary>Within one window: create groups, then join groups here, then send tabs to other windows.</summary>
    private static int Phase(GroupMove move) => move.New ? 0 : move.Crosses ? 2 : 1;

    /// <summary>
    /// Rank per window number: windows that create a group go first, so every later join finds its group, then the
    /// rest; each set in the order the windows were given.
    /// </summary>
    private static Dictionary<int, int> ExecutionOrder(IEnumerable<GroupMove> moves)
    {
        var all = moves.ToList();
        var creating = all.Where(move => move.New).Select(move => move.WindowNumber).Distinct().Order().ToList();
        var rest = all.Select(move => move.WindowNumber).Distinct().Order();
        return creating.Concat(rest.Except(creating)).Select((number, rank) => (number, rank)).ToDictionary(pair => pair.number, pair => pair.rank);
    }

    [GeneratedRegex($@" · (?<owner>{Name})/(?<repo>{Name})(?:@\w+)?(?: · GitHub)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingRepo();

    [GeneratedRegex($@"^(?:GitHub - )?(?<owner>{Name})/(?<repo>{Name})(?::\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex HomeRepo();

    private sealed record Context(int MinSize, UrlOwners Owners, SiteHosts Sites);

    /// <summary>A window with its 1-based number in the order the windows were given.</summary>
    private sealed record Numbered(int Number, ChromeWindow Window);

    private sealed record Found(ChromeWindow Window, int Number, ChromeTab Tab);

    private sealed record KeySpec(string Key, bool Repo)
    {
        public required string Title { get; set; }

        public List<Found> Tabs { get; } = [];
    }
}
