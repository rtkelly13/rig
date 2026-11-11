using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>How a declared app stands on one account.</summary>
/// <param name="AppSlug">The app's slug.</param>
/// <param name="AppName">Its display name.</param>
/// <param name="Account">The account audited.</param>
/// <param name="Required">Whether the policy requires it there.</param>
/// <param name="Installed">Whether it is installed there.</param>
/// <param name="InstallationId">The installation's id, when installed.</param>
/// <param name="InstallPage">Where a person installs it.</param>
/// <param name="Selection"><c>all</c>, <c>selected</c> or <c>none</c> (not installed).</param>
/// <param name="ExpectedRepos">The repos the policy wants it to reach.</param>
/// <param name="ActualRepos">The repos it reaches; <c>all</c> alone when it reaches every one.</param>
/// <param name="MissingRepos">Expected repos it does not reach.</param>
/// <param name="IsAligned">Whether it conforms.</param>
/// <param name="Details">A line or two saying why.</param>
public sealed record AppAudit(
    string AppSlug,
    string AppName,
    string Account,
    bool Required,
    bool Installed,
    long? InstallationId,
    string InstallPage,
    string Selection,
    IReadOnlyList<string> ExpectedRepos,
    IReadOnlyList<string> ActualRepos,
    IReadOnlyList<string> MissingRepos,
    bool IsAligned,
    IReadOnlyList<string> Details);

/// <summary>What syncing an app's repo connections did.</summary>
/// <param name="Ok">False when a lookup or a write failed.</param>
/// <param name="Messages">A line per step.</param>
public sealed record AppSync(bool Ok, IReadOnlyList<string> Messages);

/// <summary>
/// The GitHub Apps declared under <c>github_apps</c>: whether each is installed on an account and reaches the repos it
/// should, and connecting or disconnecting repos to match. Ported from <c>rig.repo_governance.github_apps_manager</c>.
/// An app cannot be installed by API; only an installed one's repo selection is synced.
/// </summary>
/// <param name="api">The GitHub REST seam.</param>
/// <param name="policies">The repo policy declaring the apps.</param>
/// <param name="discover">An account's repos as <c>owner/name</c>, for an <c>all_repos</c> policy.</param>
public sealed class GitHubApps(IGitHubApi api, RepoPolicies policies, Func<string, CancellationToken, Task<IReadOnlyList<string>>> discover)
{
    private const int PageSize = 100;

    /// <summary>
    /// Each app declared for <paramref name="account"/> and how it stands; empty when none is declared. Throws
    /// <see cref="GitHubReadException"/> when the installations cannot be read, so a failed lookup never reads as
    /// "not installed".
    /// </summary>
    public async Task<IReadOnlyList<AppAudit>> AuditAsync(string account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var declared = policies.AppsForAccount(account);
        if (declared.Count == 0)
        {
            return [];
        }

        var installed = new Dictionary<string, Installation>(StringComparer.Ordinal);
        foreach (var installation in await InstallationsAsync(account, cancellationToken).ConfigureAwait(false))
        {
            if (installation.AppSlug is { Length: > 0 } slug)
            {
                installed[slug.ToLowerInvariant()] = installation;
            }
        }

        var results = new List<AppAudit>();
        foreach (var app in declared)
        {
            if (app.Accounts.GetValueOrDefault(account) is not { } policy)
            {
                continue;
            }

            var expected = policy.EffectiveRepos(await AvailableAsync(account, policy, cancellationToken).ConfigureAwait(false));
            if (!installed.TryGetValue(app.Slug.ToLowerInvariant(), out var installation))
            {
                results.Add(new(app.Slug, app.Name, account, policy.Required, false, null, app.InstallPage, "none", expected, [], expected, !policy.Required, [$"Missing installation on account '{account}'."]));
                continue;
            }

            if (installation.Selection == "all")
            {
                results.Add(new(app.Slug, app.Name, account, policy.Required, true, installation.Id, app.InstallPage, "all", expected, ["all"], [], true, [$"App is enabled on all repositories across '{account}'."]));
                continue;
            }

            var actual = await InstallationReposAsync(installation.Id, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> missing = [.. expected.Where(repo => !actual.Contains(repo))];
            var detail = missing.Count > 0
                ? $"Repositories missing connection: {string.Join(", ", missing)}"
                : $"All {expected.Count} expected repositories are connected.";
            results.Add(new(app.Slug, app.Name, account, policy.Required, true, installation.Id, app.InstallPage, installation.Selection, expected, actual, missing, missing.Count == 0, [detail]));
        }

        return results;
    }

    /// <summary>
    /// Connects <paramref name="slug"/>'s installation on <paramref name="account"/> to each expected repo it lacks and
    /// disconnects each disallowed one it has; on a dry run, says what it would do.
    /// </summary>
    public async Task<AppSync> SyncAsync(string account, string slug, bool dryRun, CancellationToken cancellationToken = default)
    {
        var app = policies.Apps.FirstOrDefault(declared => declared.Slug == slug);
        if (app is null)
        {
            return new(false, [$"App '{slug}' is not declared in policy configuration."]);
        }

        if (app.Accounts.GetValueOrDefault(account) is not { } policy)
        {
            return new(false, [$"Account '{account}' has no declared policy for app '{slug}'."]);
        }

        IReadOnlyList<Installation> installations;
        try
        {
            installations = await InstallationsAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubReadException e)
        {
            return new(false, [$"Cannot sync repositories: installation lookup failed: {e.Message}"]);
        }

        if (installations.FirstOrDefault(i => string.Equals(i.AppSlug ?? string.Empty, slug, StringComparison.OrdinalIgnoreCase)) is not { } installation)
        {
            return new(false, [$"Cannot sync repositories: App '{app.Name}' ({slug}) is NOT installed on '{account}'.", $"Please install it first: {app.InstallPage}"]);
        }

        if (installation.Selection == "all")
        {
            return new(true, [$"App '{slug}' has access to all repositories on '{account}' (selection='all')."]);
        }

        var expected = policy.EffectiveRepos(await AvailableAsync(account, policy, cancellationToken).ConfigureAwait(false));
        IReadOnlyList<string> actual;
        try
        {
            actual = await InstallationReposAsync(installation.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubReadException e)
        {
            return new(false, [$"Cannot sync repositories: repository lookup failed: {e.Message}"]);
        }

        var missing = expected.Where(repo => !actual.Contains(repo)).ToList();
        var disallowed = actual.Where(policy.DisallowRepos.Contains).ToList();
        if (missing.Count == 0 && disallowed.Count == 0)
        {
            return new(true, [$"All declared repositories are correctly connected to '{slug}'."]);
        }

        var messages = new List<string>();
        var ok = true;
        foreach (var (repo, connect) in missing.Select(repo => (repo, true)).Concat(disallowed.Select(repo => (repo, false))))
        {
            if (await RepoIdAsync(account, repo, cancellationToken).ConfigureAwait(false) is not { } id)
            {
                ok = false;
                messages.Add($"Could not resolve repository ID for {account}/{repo}");
                continue;
            }

            var (done, message) = await ConnectAsync(installation.Id, id, connect, dryRun, cancellationToken).ConfigureAwait(false);
            ok &= done;
            messages.Add(connect ? $"{repo}: {message}" : $"{repo} (disallowed): {message}");
        }

        return new(ok, messages);
    }

    private async Task<(bool Ok, string Message)> ConnectAsync(long installation, long repo, bool connect, bool dryRun, CancellationToken cancellationToken)
    {
        var (verb, preposition) = connect ? ("add", "to") : ("remove", "from");
        if (dryRun)
        {
            return (true, $"dry-run: would {verb} repo {repo} {preposition} installation {installation}");
        }

        var route = string.Create(CultureInfo.InvariantCulture, $"user/installations/{installation}/repositories/{repo}");
        var ran = await GitHubCalls.SendAsync(api, connect ? "PUT" : "DELETE", route, null, cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            return (false, connect ? $"Failed to connect repo {repo}: {ran.Err.Trim()}" : $"Failed to remove repo {repo}: {ran.Err.Trim()}");
        }

        return (true, connect
            ? $"Successfully connected repo {repo} to installation {installation}"
            : $"Successfully removed repo {repo} from installation {installation}");
    }

    private async Task<long?> RepoIdAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        try
        {
            return GitHubCalls.Number(await GitHubCalls.ReadJsonAsync(api, $"repos/{owner}/{repo}", cancellationToken).ConfigureAwait(false), "id") is > 0 and var id ? id : null;
        }
        catch (GitHubReadException)
        {
            return null;
        }
    }

    // The repos an all_repos policy covers: those the policy registers, then those GitHub lists for the account.
    private async Task<IReadOnlyList<string>> AvailableAsync(string account, AppAccountPolicy policy, CancellationToken cancellationToken)
    {
        var names = policies.ReposForOrg(account).ToList();
        if (!policy.AllRepos)
        {
            return names;
        }

        foreach (var full in await discover(account, cancellationToken).ConfigureAwait(false))
        {
            var slash = full.LastIndexOf('/');
            var (owner, bare) = slash < 0 ? (string.Empty, full) : (full[..slash], full[(slash + 1)..]);
            if ((owner.Length == 0 || string.Equals(owner, account, StringComparison.OrdinalIgnoreCase)) && !names.Contains(bare))
            {
                names.Add(bare);
            }
        }

        return names;
    }

    // An organisation's installations, or the signed-in user's filtered to the account for a personal one.
    private async Task<IReadOnlyList<Installation>> InstallationsAsync(string account, CancellationToken cancellationToken)
    {
        var user = await GitHubCalls.ReadJsonAsync(api, $"users/{account}", cancellationToken).ConfigureAwait(false);
        var isOrg = GitHubCalls.Str(user, "type") == "Organization";
        var entries = await PagesAsync(isOrg ? $"orgs/{account}/installations" : "user/installations", "installations", cancellationToken).ConfigureAwait(false);
        var installations = new List<Installation>();
        foreach (var entry in entries)
        {
            var id = GitHubCalls.Number(entry, "id") ?? throw new GitHubReadException($"an installation of {account} has no id");
            var login = GitHubCalls.Str(entry?["account"], "login") ?? string.Empty;
            if (isOrg || string.Equals(login, account, StringComparison.OrdinalIgnoreCase))
            {
                installations.Add(new(id, GitHubCalls.Str(entry, "app_slug"), GitHubCalls.Str(entry, "repository_selection") is { Length: > 0 } selection ? selection : "selected"));
            }
        }

        return installations;
    }

    private async Task<IReadOnlyList<string>> InstallationReposAsync(long installation, CancellationToken cancellationToken)
    {
        var route = string.Create(CultureInfo.InvariantCulture, $"user/installations/{installation}/repositories");
        var repos = await PagesAsync(route, "repositories", cancellationToken).ConfigureAwait(false);
        return [.. repos.Select(repo => GitHubCalls.Str(repo, "name")?.Trim()).OfType<string>().Where(name => name.Length > 0)];
    }

    // Every entry under key, read a page at a time until a short page.
    private async Task<List<JsonNode?>> PagesAsync(string route, string key, CancellationToken cancellationToken)
    {
        var entries = new List<JsonNode?>();
        for (var number = 1; ; number++)
        {
            var paged = string.Create(CultureInfo.InvariantCulture, $"{route}?per_page={PageSize}&page={number}");
            var page = (await GitHubCalls.ReadJsonAsync(api, paged, cancellationToken).ConfigureAwait(false))[key] as JsonArray
                ?? throw new GitHubReadException($"GET {paged}: no {key} list");
            entries.AddRange(page);
            if (page.Count < PageSize)
            {
                return entries;
            }
        }
    }

    private sealed record Installation(long Id, string? AppSlug, string Selection);
}
