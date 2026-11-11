using System.Globalization;
using Rig.Core.Packages;

namespace Rig.Core.Orca;

/// <summary>One difference, or agreement, between the policy and Orca.</summary>
/// <param name="Subject">What it is about: <c>settings</c>, <c>agent &lt;name&gt;</c>, <c>repo &lt;name&gt;</c>.</param>
/// <param name="Check">Which setting.</param>
/// <param name="Status">How it stands.</param>
/// <param name="Detail">What Orca has, and what is wanted when they differ.</param>
/// <param name="Fix">How to make them agree; <c>""</c> when they do.</param>
public sealed record OrcaFinding(string Subject, string Check, CheckStatus Status, string Detail, string Fix = "");

/// <summary>Where the policy's paths resolve on this machine.</summary>
/// <param name="EstateRoot">The estate root, where unpathed repos and managed projects are cloned.</param>
/// <param name="CodeRoot">This machine's code root, which <c>~/code</c> in the policy stands for.</param>
/// <param name="Home">The home folder <c>~</c> expands to.</param>
public sealed record OrcaPaths(string EstateRoot, string CodeRoot, string Home)
{
    /// <summary>
    /// A repo path from the policy. The policy is shared across machines and spells every repo <c>~/code/&lt;group&gt;/&lt;name&gt;</c>,
    /// but a machine may keep its clones elsewhere, so <c>~/code</c> is the code root.
    /// </summary>
    public string RepoPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = Parts(path);
        return parts is ["~", "code", ..] ? Posix(string.Join('/', [CodeRoot, .. parts[2..]])) : Expand(path);
    }

    /// <summary><paramref name="path"/> with a leading <c>~</c> expanded and the result normalised, as Python's <c>Path.expanduser()</c>.</summary>
    public string Expand(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Posix(Home + path[1..])
            : Posix(path);
    }

    /// <summary><paramref name="path"/> normalised as Python's <c>Path(path).as_posix()</c>: forward slashes, no empty or <c>.</c> segments.</summary>
    public static string Posix(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var unified = path.Replace('\\', '/');
        var rooted = unified.StartsWith('/');
        var joined = string.Join('/', Parts(unified));
        return rooted ? "/" + joined : joined.Length == 0 ? "." : joined;
    }

    private static string[] Parts(string path) =>
        [.. path.Replace('\\', '/').Split('/').Where(part => part.Length > 0 && part != ".")];
}

/// <summary><c>workspace orca doctor</c>: every difference between <c>config/orca.json</c> and Orca.</summary>
public static class OrcaDoctor
{
    /// <summary>How old a settings copy may be before the doctor warns that recent changes may not show.</summary>
    public const double StaleAfterHours = 24;

    /// <summary>The backups Orca writes beside its live store.</summary>
    public const string BackupGlob = "profile-state.db.backup.*.db";

    /// <summary>Every difference between the policy and Orca, plus local managed projects Orca has not registered.</summary>
    public static IReadOnlyList<OrcaFinding> Diagnose(
        OrcaPolicy policy, OrcaSnapshot snapshot, OrcaPaths paths, IReadOnlyCollection<string> managedProjects, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(managedProjects);
        List<OrcaFinding> findings = [.. SettingsFindings(policy, snapshot.Settings, snapshot.ProfileDir, paths, now)];
        findings.AddRange(AgentChecks(policy, snapshot).Select(Finding));
        if (policy.AgentPermissions is not null && snapshot.LaunchArgs is null)
        {
            findings.Add(new("agents", "launch args", CheckStatus.Skip, "Orca is not running to ask"));
        }

        findings.AddRange(RepoFindings(policy, snapshot.Repos, paths, managedProjects));
        return findings;
    }

    /// <summary>Custom arguments and permission modes from the stored settings, and launch arguments from the running Orca.</summary>
    public static IReadOnlyList<AgentCheck> AgentChecks(OrcaPolicy policy, OrcaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);
        var stored = snapshot.Settings?.Settings;
        List<AgentCheck> checks = stored is null ? [] : [.. AgentPermissions.CustomArgsChecks(policy.AgentArgs, stored.AgentDefaultArgs ?? new Dictionary<string, string>())];
        if (policy.AgentPermissions is not { } want)
        {
            return checks;
        }

        if (stored is not null)
        {
            var overrides = stored.PermissionOverrides ?? new Dictionary<string, string>();
            checks.AddRange(AgentPermissions.PermissionChecks(want, overrides.Keys, stored.PermissionDefault, overrides));
        }

        if (snapshot.LaunchArgs is not null)
        {
            checks.AddRange(AgentPermissions.LaunchChecks(want, policy.AgentArgs, snapshot.LaunchArgs));
        }

        return checks;
    }

    /// <summary>The scripts the policy names for a repo, trimmed: setup first, then archive.</summary>
    internal static IEnumerable<(string Kind, string Script)> WantedScripts(RepoPolicy want)
    {
        if (want.Setup is not null)
        {
            yield return ("setup", want.Setup.Trim());
        }

        if (want.Archive is not null)
        {
            yield return ("archive", want.Archive.Trim());
        }
    }

    private static OrcaFinding Finding(AgentCheck check) => check.Ok
        ? new(check.Subject, check.Check, CheckStatus.Ok, check.Have.Length > 0 ? check.Have : "(none)")
        : new(check.Subject, check.Check, CheckStatus.Fail, $"\"{check.Have}\", want \"{check.Want}\"", check.Fix);

    private static List<OrcaFinding> SettingsFindings(OrcaPolicy policy, SettingsCopy? copy, string profileDir, OrcaPaths paths, DateTimeOffset now)
    {
        if (copy is null)
        {
            return [new("settings", "source", CheckStatus.Skip, $"no readable {BackupGlob} in {OrcaPaths.Posix(profileDir)}", "start Orca once so it writes a settings backup")];
        }

        var ageHours = (now - copy.Saved).TotalSeconds / 3600;
        var stale = ageHours > StaleAfterHours;
        List<OrcaFinding> findings =
        [
            new(
                "settings",
                "source",
                stale ? CheckStatus.Warn : CheckStatus.Ok,
                string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(copy.Source)}, saved {ageHours:F1}h ago"),
                stale ? "Orca writes these backups as it saves; recent Settings changes may not show yet" : ""),
        ];
        if (policy.WorkspaceDir is not null)
        {
            var want = paths.Expand(policy.WorkspaceDir);
            var have = string.IsNullOrEmpty(copy.Settings.WorkspaceDir) ? null : paths.Expand(copy.Settings.WorkspaceDir);
            var ok = have == want;
            findings.Add(new(
                "settings",
                "workspace dir",
                ok ? CheckStatus.Ok : CheckStatus.Fail,
                (have ?? "unset") + (ok ? "" : $", want {want}"),
                ok ? "" : $"Orca Settings: set the workspace folder to {want}"));
        }

        if (policy.AgentSleep is not null)
        {
            var check = AgentSleepCheck.Of(policy.AgentSleep, copy.Settings.AgentSleep, copy.Settings.AgentSleepIdleMs);
            findings.Add(check.Ok
                ? new("settings", "agent sleep", CheckStatus.Ok, check.Have)
                : new("settings", "agent sleep", CheckStatus.Fail, $"{check.Have}, want {check.Want}", check.Fix));
        }

        return findings;
    }

    private static List<OrcaFinding> RepoFindings(OrcaPolicy policy, IReadOnlyList<OrcaRepo>? repos, OrcaPaths paths, IReadOnlyCollection<string> managedProjects)
    {
        if (repos is null)
        {
            return [new("repos", "source", CheckStatus.Skip, "`orca repo list --json` failed", "run `orca open`")];
        }

        var byName = ByName(repos);
        List<OrcaFinding> findings = [];
        foreach (var (name, want) in policy.Repos.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (byName.GetValueOrDefault(name) is { } repo)
            {
                findings.AddRange(RepoPolicyFindings(name, repo, want));
                continue;
            }

            var path = want.Path is not null ? paths.RepoPath(want.Path) : OrcaPaths.Posix(Path.Join(paths.EstateRoot, name));
            findings.Add(new($"repo {name}", "registered", CheckStatus.Fail, "named in config/orca.json but not registered in Orca", $"orca repo add --path {path}"));
        }

        var registered = repos.Select(repo => OrcaPaths.Posix(repo.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var name in managedProjects)
        {
            var path = OrcaPaths.Posix(Path.Join(paths.EstateRoot, name));
            if (Path.Exists(Path.Join(path, ".git")) && !registered.Contains(path) && !policy.Repos.ContainsKey(name))
            {
                findings.Add(new($"repo {name}", "registered", CheckStatus.Warn, "managed project cloned locally but not registered in Orca", $"orca repo add --path {path}"));
            }
        }

        return findings;
    }

    /// <summary>Repos by display name; a later repo with the same name wins, as a Python dict comprehension keeps it.</summary>
    internal static Dictionary<string, OrcaRepo> ByName(IReadOnlyList<OrcaRepo> repos)
    {
        var byName = new Dictionary<string, OrcaRepo>(StringComparer.Ordinal);
        foreach (var repo in repos)
        {
            byName[repo.DisplayName] = repo;
        }

        return byName;
    }

    private static List<OrcaFinding> RepoPolicyFindings(string name, OrcaRepo repo, RepoPolicy want)
    {
        var subject = $"repo {name}";
        var shared = OrcaHooks.ReadOrcaYaml(repo.Path);
        List<OrcaFinding> findings = [.. WantedScripts(want).Select(wanted => ScriptFinding(name, wanted.Kind, wanted.Script, repo.HookSettings, shared))];
        var path = OrcaPaths.Posix(repo.Path);
        if (want.ExternalWorktreeVisibility is { } visibility)
        {
            var have = repo.ExternalWorktreeVisibility ?? "inherit";
            findings.Add(Compare(subject, "external worktrees", have == visibility, have, visibility, $"orca repo set --repo path:{path} --external-worktree-visibility {visibility}"));
        }

        if (want.BaseRef is { } baseRef)
        {
            findings.Add(Compare(subject, "base ref", repo.BaseRef == baseRef, Or(repo.BaseRef, "(none)"), baseRef, $"orca repo set-base-ref --repo path:{path} --ref {baseRef}"));
        }

        if (want.WorktreeBasePath is { } basePath)
        {
            var have = (repo.WorktreeBasePath ?? "").Trim();
            findings.Add(Compare(subject, "worktree location", have == basePath.Trim(), Or(have, "(global workspace folder)"), basePath, $"run `workspace orca sync --apply` to set the worktree location to {basePath}"));
        }

        if (want.Color is { } color)
        {
            var ok = string.Equals(repo.Color.ToLowerInvariant(), color.ToLowerInvariant(), StringComparison.Ordinal);
            findings.Add(Compare(subject, "badge color", ok, Or(repo.Color, "(none)"), color, $"run `workspace orca sync --apply` to set badge color to {color}"));
        }

        if (want.Icon is { } icon)
        {
            findings.Add(OrcaRepoIcon.Renders(icon)
                ? Compare(subject, "icon", repo.IconMatches(icon), Or(repo.IconIdentifier, "(none)"), icon, $"run `workspace orca sync --apply` to set repo icon to {icon}")
                : new(subject, "icon", CheckStatus.Fail, $"{icon} is not an icon Orca draws; it shows a folder", "use an emoji: icon, an image URL, or one of " + string.Join(", ", OrcaRepoIcon.Lucide)));
        }

        return findings;
    }

    private static OrcaFinding ScriptFinding(string name, string kind, string want, HookSettings? local, Scripts? shared)
    {
        var source = OrcaHooks.Resolve(kind, local, shared);
        var (subject, check) = ($"repo {name}", $"{kind} script");
        if (source.Shared == want)
        {
            return source.Policy != "local-only" && source.Effective == want
                ? new(subject, check, CheckStatus.Ok, $"\"{want}\" from {OrcaHooks.OrcaYaml}")
                : new(
                    subject,
                    check,
                    source.Effective == want ? CheckStatus.Warn : CheckStatus.Fail,
                    $"Orca's local script \"{source.Local}\" overrides {OrcaHooks.OrcaYaml}",
                    "run `workspace orca sync --apply` to clear the local script");
        }

        var commitFix = $"commit {OrcaHooks.OrcaYaml} to {name} declaring scripts.{kind}: {want}";
        if (source.Effective == want)
        {
            return new(subject, check, CheckStatus.Warn, $"\"{want}\" is set only in Orca's local settings, not {OrcaHooks.OrcaYaml}", commitFix);
        }

        var sharedNote = source.Shared.Length > 0 ? $"{OrcaHooks.OrcaYaml} has \"{source.Shared}\"" : $"not in {OrcaHooks.OrcaYaml}";
        return new(subject, check, CheckStatus.Fail, $"\"{source.Effective}\" ({sharedNote}), want \"{want}\"", commitFix);
    }

    private static OrcaFinding Compare(string subject, string check, bool ok, string have, string want, string fix) =>
        ok ? new(subject, check, CheckStatus.Ok, have) : new(subject, check, CheckStatus.Fail, $"{have}, want {want}", fix);

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
}
