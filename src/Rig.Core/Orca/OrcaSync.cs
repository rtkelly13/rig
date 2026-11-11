using System.Text.Json.Nodes;
using Rig.Core.Vault;

namespace Rig.Core.Orca;

/// <summary>One change sync plans or makes.</summary>
/// <param name="Repo">What it changes: a repo's display name, <c>agent &lt;name&gt;</c>, <c>settings</c> or <c>all</c>.</param>
/// <param name="Action"><c>register</c>, <c>update</c>, <c>manual</c> or <c>error</c>.</param>
/// <param name="Field">Which setting.</param>
/// <param name="OldValue">What Orca has.</param>
/// <param name="NewValue">What the policy wants.</param>
/// <param name="Applied">Whether it was made.</param>
/// <param name="Detail">Why not, or what follows.</param>
public sealed record SyncAction(string Repo, string Action, string Field, string OldValue, string NewValue, bool Applied, string Detail = "");

/// <summary>
/// The <c>repo.update</c> fields sync sends; null ones are left out. Orca merges top-level fields but replaces
/// <c>hookSettings</c> whole, and stores a <c>""</c> worktree location as unset.
/// </summary>
/// <param name="BadgeColor">The badge colour.</param>
/// <param name="RepoIcon">The icon.</param>
/// <param name="HookSettings">The whole hook settings.</param>
/// <param name="WorktreeBasePath">The worktree location; relative paths resolve from the repo root.</param>
public sealed record RepoUpdates(
    string? BadgeColor = null,
    OrcaRepoIcon? RepoIcon = null,
    HookSettings? HookSettings = null,
    string? WorktreeBasePath = null)
{
    /// <summary>The updates as <c>repo.update</c> takes them.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (BadgeColor is not null)
        {
            json["badgeColor"] = BadgeColor;
        }

        if (RepoIcon is not null)
        {
            json["repoIcon"] = RepoIcon.ToJson();
        }

        if (HookSettings is not null)
        {
            json["hookSettings"] = HookSettings.ToJson();
        }

        if (WorktreeBasePath is not null)
        {
            json["worktreeBasePath"] = WorktreeBasePath;
        }

        return json;
    }
}

/// <summary>The <c>orca</c> CLI, pointed at one Orca instance.</summary>
public interface IOrcaCli
{
    /// <summary>Stdout of <c>orca &lt;args&gt;</c>, or null when the CLI is missing or fails.</summary>
    Task<string?> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>The running Orca's runtime socket, for what its CLI cannot set.</summary>
public interface IOrcaLive
{
    /// <summary>Sends one <c>repo.update</c>; false when Orca is unreachable or refuses it.</summary>
    Task<bool> UpdateRepoAsync(string repoPath, RepoUpdates updates, CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="lines"/> merged onto the live launch lines, since Orca replaces <c>agentDefaultArgs</c> whole;
    /// returns every line as Orca then serves it, or null when it could not be reached.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> SendLaunchLinesAsync(IReadOnlyDictionary<string, string> lines, CancellationToken cancellationToken);
}

/// <summary>
/// <c>workspace orca sync</c>: plans, and with apply makes, the changes that bring Orca in line with the policy. Only
/// fields that differ are sent. Scripts are never written from the policy: they belong in each repo's committed
/// <c>orca.yaml</c>, so a missing one is manual work.
/// </summary>
public static class OrcaSync
{
    private const string SocketFailure = "Orca's socket refused or was unreachable; these settings need Orca running";
    private const string Unreachable = "Orca's runtime could not be reached; these settings need Orca running";

    private static readonly string[] ScriptKinds = ["setup", "archive"];

    /// <summary>
    /// Agent sleep as a <c>manual</c> row: <c>settings.update</c> rejects its keys and Orca's database must not be edited
    /// while it runs, so sync names the change to make in Orca's Settings.
    /// </summary>
    public static IReadOnlyList<SyncAction> SettingsManualActions(OrcaPolicy policy, OrcaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (policy.AgentSleep is null || snapshot.Settings is null)
        {
            return [];
        }

        var settings = snapshot.Settings.Settings;
        var check = AgentSleepCheck.Of(policy.AgentSleep, settings.AgentSleep, settings.AgentSleepIdleMs);
        return check.Ok ? [] : [new("settings", "manual", "agent sleep", check.Have, check.Want, false, check.Fix)];
    }

    /// <summary>
    /// Plans, and with <paramref name="apply"/> sends, each policy agent's launch line. Orca splits each line into the
    /// agent's mode and custom arguments; a line it does not serve back as sent is an error, since Orca keeps a mode
    /// when a line with no flags is sent unchanged.
    /// </summary>
    public static async Task<IReadOnlyList<SyncAction>> SyncAgentLaunchAsync(
        OrcaPolicy policy, OrcaSnapshot snapshot, bool apply, IOrcaLive? live, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (policy.AgentPermissions is not { } want || snapshot.LaunchArgs is not { } have)
        {
            return [];
        }

        var lines = AgentPermissions.LaunchLines(want, policy.AgentArgs, have);
        if (lines.Count == 0)
        {
            return [];
        }

        if (!apply || live is null)
        {
            return [.. lines.Select(pair => Launch(pair.Key, have, pair.Value, false, "planned"))];
        }

        var served = await live.SendLaunchLinesAsync(lines, cancellationToken).ConfigureAwait(false);
        if (served is null)
        {
            return [.. lines.Select(pair => Launch(pair.Key, have, pair.Value, false, Unreachable))];
        }

        return [.. lines.Select(pair =>
        {
            var now = served.GetValueOrDefault(pair.Key, "");
            var taken = Tokens(now) == Tokens(pair.Value);
            return Launch(pair.Key, have, pair.Value, taken, taken ? "" : $"Orca serves {PyText.Repr(now)}; set this agent in Orca Settings → Agents");
        })];
    }

    /// <summary>
    /// Plans, and with <paramref name="apply"/> makes, the changes that bring Orca's repos in line with the policy. A
    /// <paramref name="retired"/> project is never registered: <c>estate orca</c> removes it daily, and registering it
    /// again would undo that every day.
    /// </summary>
    public static async Task<IReadOnlyList<SyncAction>> SyncReposAsync(
        OrcaPolicy policy,
        OrcaSnapshot snapshot,
        OrcaPaths paths,
        bool apply,
        string? targetRepo,
        IOrcaCli cli,
        IOrcaLive live,
        IReadOnlySet<string> retired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(retired);
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(live);
        if (snapshot.Repos is null)
        {
            return [new("all", "error", "repos", "", "", false, "Orca repos could not be listed (run `orca open` first)")];
        }

        var byName = OrcaDoctor.ByName(snapshot.Repos);
        List<SyncAction> actions = [];
        foreach (var (name, want) in policy.Repos.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (targetRepo is not null && name != targetRepo)
            {
                continue;
            }

            if (byName.GetValueOrDefault(name) is not { } repo)
            {
                actions.Add(retired.Contains(name)
                    ? new(name, "manual", "registered", "unregistered", "", false, "retired; drop it from orca.json")
                    : await RegisterAsync(name, want.Path is not null ? paths.RepoPath(want.Path) : OrcaPaths.Posix(Path.Join(paths.EstateRoot, name)), apply, cli, cancellationToken).ConfigureAwait(false));
                continue;
            }

            var context = new Context(name, repo, apply, cli, live, cancellationToken);
            actions.AddRange(await SyncScriptsAsync(context, want).ConfigureAwait(false));
            actions.AddRange(await SyncCliFieldsAsync(context, want).ConfigureAwait(false));
            actions.AddRange(await SyncBadgeAsync(context, want).ConfigureAwait(false));
        }

        return actions;
    }

    private static SyncAction Launch(string agent, IReadOnlyDictionary<string, string> have, string line, bool applied, string detail) =>
        new($"agent {agent}", applied || detail == "planned" ? "update" : "error", "launch args", have.GetValueOrDefault(agent, ""), line, applied, detail);

    private static string Tokens(string line) =>
        string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    private static async Task<SyncAction> RegisterAsync(string name, string path, bool apply, IOrcaCli cli, CancellationToken cancellationToken)
    {
        if (!Path.Exists(path))
        {
            return new(name, "error", "registered", "unregistered", path, false, $"{path} does not exist");
        }

        if (!apply)
        {
            return new(name, "register", "registered", "unregistered", path, false, $"would add {path}");
        }

        return await cli.RunAsync(["repo", "add", "--path", path], cancellationToken).ConfigureAwait(false) is null
            ? new(name, "error", "registered", "unregistered", path, false, "`orca repo add` failed")
            : new(name, "register", "registered", "unregistered", path, true, "added; run sync again for its settings");
    }

    // Clears local scripts that shadow a matching orca.yaml; reports scripts orca.yaml lacks.
    private static async Task<List<SyncAction>> SyncScriptsAsync(Context context, RepoPolicy want)
    {
        var local = context.Repo.HookSettings;
        var shared = OrcaHooks.ReadOrcaYaml(context.Repo.Path);
        List<SyncAction> manual = [];
        List<Diff> shadowed = [];
        foreach (var (kind, script) in OrcaDoctor.WantedScripts(want))
        {
            var source = OrcaHooks.Resolve(kind, local, shared);
            var field = $"{kind} script";
            if (source.Shared == script)
            {
                if (source.Policy == "local-only" || source.Effective != script)
                {
                    shadowed.Add(new(field, Or(source.Local, "(none)"), $"{OrcaHooks.OrcaYaml}: {script}"));
                }
            }
            else if (source.Effective != script)
            {
                manual.Add(new(context.Name, "manual", field, Or(source.Effective, "(none)"), script, false, $"commit {OrcaHooks.OrcaYaml} to {context.Name} declaring scripts.{kind}; sync does not write repo files"));
            }
        }

        if (shadowed.Count == 0)
        {
            return manual;
        }

        // Clearing drops every local script, so it is safe only when orca.yaml covers each one.
        var orphaned = ScriptKinds.Where(kind => OrcaHooks.Resolve(kind, local, shared) is { Local.Length: > 0, Shared.Length: 0 }).ToList();
        if (orphaned.Count > 0)
        {
            var detail = $"local {string.Join(", ", orphaned)} script has no {OrcaHooks.OrcaYaml} equivalent; clearing would lose it";
            return [.. manual, .. shadowed.Select(diff => new SyncAction(context.Name, "manual", diff.Field, diff.Old, diff.New, false, detail))];
        }

        var ok = context.Apply && await context.Live.UpdateRepoAsync(context.Repo.Path, new RepoUpdates(HookSettings: OrcaHooks.DeferToOrcaYaml(local)), context.CancellationToken).ConfigureAwait(false);
        return [.. manual, .. shadowed.Select(diff => context.Outcome(diff, ok, SocketFailure))];
    }

    // Visibility and base ref, through the documented `orca repo` commands.
    private static async Task<List<SyncAction>> SyncCliFieldsAsync(Context context, RepoPolicy want)
    {
        List<SyncAction> actions = [];
        var path = $"path:{context.Repo.Path}";
        var haveVisibility = context.Repo.ExternalWorktreeVisibility ?? "inherit";
        if (want.ExternalWorktreeVisibility is { } visibility && haveVisibility != visibility)
        {
            var ok = context.Apply && await context.Cli.RunAsync(["repo", "set", "--repo", path, "--external-worktree-visibility", visibility], context.CancellationToken).ConfigureAwait(false) is not null;
            actions.Add(context.Outcome(new("external worktrees", haveVisibility, visibility), ok, "`orca repo set` failed"));
        }

        if (want.BaseRef is { } baseRef && context.Repo.BaseRef != baseRef)
        {
            var ok = context.Apply && await context.Cli.RunAsync(["repo", "set-base-ref", "--repo", path, "--ref", baseRef], context.CancellationToken).ConfigureAwait(false) is not null;
            actions.Add(context.Outcome(new("base ref", Or(context.Repo.BaseRef, "(none)"), baseRef), ok, "`orca repo set-base-ref` failed"));
        }

        return actions;
    }

    // Fields only repo.update can set (colour, icon, worktree location), in one call carrying just the changes.
    private static async Task<List<SyncAction>> SyncBadgeAsync(Context context, RepoPolicy want)
    {
        var repo = context.Repo;
        var color = want.Color is not null && !string.Equals(repo.Color.ToLowerInvariant(), want.Color.ToLowerInvariant(), StringComparison.Ordinal) ? want.Color : null;
        var icon = want.Icon is not null && !repo.IconMatches(want.Icon) ? want.Icon : null;
        var haveBase = (repo.WorktreeBasePath ?? "").Trim();
        var wantBase = want.WorktreeBasePath?.Trim();
        var basePath = wantBase is not null && wantBase != haveBase ? wantBase : null;
        List<Diff> diffs = [];
        if (color is not null)
        {
            diffs.Add(new("badge color", Or(repo.Color, "(none)"), color));
        }

        if (icon is not null)
        {
            diffs.Add(new("icon", Or(repo.IconIdentifier, "(none)"), icon));
        }

        if (basePath is not null)
        {
            diffs.Add(new("worktree location", Or(haveBase, "(global)"), Or(basePath, "(global)")));
        }

        if (diffs.Count == 0)
        {
            return [];
        }

        var updates = new RepoUpdates(color, string.IsNullOrEmpty(icon) ? null : OrcaRepoIcon.For(icon), WorktreeBasePath: basePath);
        var ok = context.Apply && await context.Live.UpdateRepoAsync(repo.Path, updates, context.CancellationToken).ConfigureAwait(false);
        return [.. diffs.Select(diff => context.Outcome(diff, ok, SocketFailure))];
    }

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private sealed record Diff(string Field, string Old, string New);

    private sealed record Context(string Name, OrcaRepo Repo, bool Apply, IOrcaCli Cli, IOrcaLive Live, CancellationToken CancellationToken)
    {
        public SyncAction Outcome(Diff diff, bool ok, string failure) => !Apply
            ? new(Name, "update", diff.Field, diff.Old, diff.New, false, "planned")
            : new(Name, ok ? "update" : "error", diff.Field, diff.Old, diff.New, ok, ok ? "" : failure);
    }
}
