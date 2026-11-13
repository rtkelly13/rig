using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Orca;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>Orca could not be read: not running, or it answered with something unexpected.</summary>
public sealed class WrapupException : Exception
{
    /// <summary>Creates the exception.</summary>
    public WrapupException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public WrapupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and its cause.</summary>
    public WrapupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What wrap-up does, or did, with one agent: the strings <c>--json</c> writes.</summary>
public static class WrapupStatus
{
    /// <summary>Idle long enough, not settled: the prompt goes out on --apply.</summary>
    public const string Ask = "ask";

    /// <summary>The prompt was sent.</summary>
    public const string Sent = "sent";

    /// <summary>The prompt could not be sent.</summary>
    public const string SendFailed = "send-failed";

    /// <summary>Would be asked, but this run's --max was reached.</summary>
    public const string Deferred = "deferred";

    /// <summary>The wrap-up prompt was its last prompt and it has replied.</summary>
    public const string Answered = "answered";

    /// <summary>The wrap-up prompt was its last prompt and it is still replying.</summary>
    public const string Answering = "answering";

    /// <summary>Clean, pushed and merged: nothing to ask.</summary>
    public const string Settled = "settled";

    /// <summary>Busy.</summary>
    public const string Working = "working";

    /// <summary>Blocked on a question or a permission prompt: it is waiting for its owner.</summary>
    public const string Waiting = "waiting";

    /// <summary>Idle for less than --idle.</summary>
    public const string Recent = "recent";

    /// <summary>No live, writable agent terminal for this pane.</summary>
    public const string NoTerminal = "no-terminal";

    /// <summary>Recorded as asked, but the pane has not moved since: the prompt never arrived. Not asked again.</summary>
    public const string Unconfirmed = "unconfirmed";
}

/// <summary>Which half of the estate a run looks at.</summary>
public enum WrapupScope
{
    /// <summary>The personal and org folders.</summary>
    Personal,

    /// <summary>This machine's work folders.</summary>
    Work,
}

/// <summary>An agent's <c>programStatus</c>.</summary>
/// <param name="State">Its state.</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Message">What it is waiting on, in words.</param>
public sealed record WrapupProgramStatus(string? State, string? Kind, string? Message);

/// <summary>One agent pane, as <c>orca worktree ps</c> reports it.</summary>
/// <param name="PaneKey">Orca's <c>tabId:leafId</c>.</param>
/// <param name="State"><c>working</c>, <c>waiting</c>, <c>done</c>, …</param>
public sealed record WrapupAgent(string PaneKey, string State)
{
    /// <summary>The agent, such as <c>claude</c>.</summary>
    public string? AgentType { get; init; }

    /// <summary>Set on sub-agent panes, which belong to the agent that started them.</summary>
    public string? ParentPaneKey { get; init; }

    /// <summary>The last prompt, cut to 200 characters by Orca.</summary>
    public string? Prompt { get; init; }

    /// <summary>The session's name.</summary>
    public string? ConversationName { get; init; }

    /// <summary>The last reply.</summary>
    public string? LastMessage { get; init; }

    /// <summary>The tool it is waiting on.</summary>
    public string? ToolName { get; init; }

    /// <summary>When it entered its state, in milliseconds since the epoch.</summary>
    public double? StateStartedAt { get; init; }

    /// <summary>What it is waiting on.</summary>
    public WrapupProgramStatus? ProgramStatus { get; init; }
}

/// <summary>A worktree's linked PR.</summary>
/// <param name="Number">The PR number.</param>
/// <param name="State"><c>open</c>, <c>draft</c>, <c>merged</c> or <c>closed</c>.</param>
/// <param name="ChecksStatus">Its checks, in Orca's words.</param>
public sealed record WrapupPr(long Number, string? State = null, string? ChecksStatus = null);

/// <summary>One worktree, as <c>orca worktree ps</c> reports it.</summary>
/// <param name="WorktreeId">Orca's id.</param>
/// <param name="RepoId">The project's id.</param>
/// <param name="Path">The folder.</param>
public sealed record WrapupWorktree(string WorktreeId, string RepoId, string Path)
{
    /// <summary>The project's display name.</summary>
    public string? Repo { get; init; }

    /// <summary>The worktree's display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The branch, possibly with <c>refs/heads/</c>.</summary>
    public string? Branch { get; init; }

    /// <summary>The parent worktree's id: Orca calls this one a subtask.</summary>
    public string? ParentId { get; init; }

    /// <summary>The linked PR.</summary>
    public WrapupPr? LinkedPr { get; init; }

    /// <summary>Its agent panes.</summary>
    public IReadOnlyList<WrapupAgent>? Agents { get; init; }

    /// <summary>The display name, else the folder's name.</summary>
    public string Name => string.IsNullOrEmpty(DisplayName) ? WrapupText.BaseName(Path) : DisplayName;

    /// <summary>The branch without <c>refs/heads/</c>.</summary>
    public string BranchName => (Branch ?? "") is var b && b.StartsWith("refs/heads/", StringComparison.Ordinal) ? b["refs/heads/".Length..] : b;
}

/// <summary>One live terminal, as <c>orca terminal list</c> reports it.</summary>
/// <param name="Handle">What <c>orca terminal send</c> takes.</param>
/// <param name="TabId">Its tab.</param>
/// <param name="LeafId">Its pane in the tab.</param>
/// <param name="AgentIdentity">The agent running in it; null for a plain shell, which must never be sent a prompt.</param>
/// <param name="Writable">Whether it takes input.</param>
public sealed record WrapupTerminal(string Handle, string? TabId = null, string? LeafId = null, string? AgentIdentity = null, bool? Writable = null)
{
    /// <summary><c>tabId:leafId</c>, as Python formatted it (a missing half reads <c>None</c>).</summary>
    public string PaneKey => $"{TabId ?? "None"}:{LeafId ?? "None"}";
}

/// <summary>A registered project, as <c>orca repo list</c> reports it.</summary>
/// <param name="Id">Orca's id.</param>
/// <param name="Path">The checkout.</param>
/// <param name="DisplayName">Its name in Orca.</param>
public sealed record WrapupRepo(string Id, string Path, string DisplayName = "");

/// <summary>What one read of Orca returned.</summary>
/// <param name="Worktrees">Worktrees with their agents.</param>
/// <param name="Terminals">Live terminals.</param>
/// <param name="Repos">Registered projects.</param>
/// <param name="Truncated">Orca returned only part of the worktree list.</param>
public sealed record WrapupSnapshot(IReadOnlyList<WrapupWorktree> Worktrees, IReadOnlyList<WrapupTerminal> Terminals, IReadOnlyList<WrapupRepo> Repos, bool Truncated = false);

/// <summary>Rig's git checks for one worktree.</summary>
/// <param name="Dirty">Uncommitted changes, staged or not.</param>
/// <param name="Unpushed">Commits reachable from HEAD that are on no remote: work that only exists here.</param>
/// <param name="Merged">HEAD is already on the default branch, by ancestry. False is "unknown" after a squash merge.</param>
/// <param name="Head">HEAD's commit.</param>
public sealed record GitFacts(bool Dirty, int Unpushed, bool Merged, string Head = "");

/// <summary>What to look at.</summary>
/// <param name="Scope">Personal or work.</param>
/// <param name="Repos">Orca project display names; empty with <paramref name="AllRepos"/>.</param>
/// <param name="AllRepos">Every project in the scope.</param>
/// <param name="Worktrees">Worktree display names, folder names, paths or branches.</param>
/// <param name="Children">Follow child worktrees (Orca subtasks) of everything selected, across repos.</param>
public sealed record WrapupTarget(WrapupScope Scope, IReadOnlyList<string> Repos, bool AllRepos, IReadOnlyList<string> Worktrees, bool Children);

/// <summary>A selected worktree and its depth in the subtask tree (0 = no selected parent).</summary>
/// <param name="Worktree">The worktree.</param>
/// <param name="Depth">Its depth.</param>
public sealed record WrapupPlaced(WrapupWorktree Worktree, int Depth);

/// <summary>An agent's verdict.</summary>
/// <param name="Done">Finished.</param>
/// <param name="Detail">What is left.</param>
public sealed record WrapupVerdict(bool Done, string Detail = "");

/// <summary>How a run plans.</summary>
/// <param name="Idle">Ask only sessions idle at least this long.</param>
/// <param name="MaxSends">Asks past this many show as deferred. Null defers nothing: a rolling run asks them all.</param>
/// <param name="IncludeSettled">Ask settled worktrees too.</param>
public sealed record WrapupOptions(TimeSpan Idle, int? MaxSends, bool IncludeSettled = false)
{
    /// <summary>The agents asked. Start with Claude; others answer prompts differently.</summary>
    public IReadOnlySet<string> Agents { get; init; } = Wrapup.Agents;
}

/// <summary>One agent session and what wrap-up does with it.</summary>
/// <param name="Repo">The project's display name.</param>
/// <param name="Worktree">The worktree's name.</param>
/// <param name="WorktreeId">Orca's id for it.</param>
/// <param name="Path">Its folder.</param>
/// <param name="Branch">Its branch.</param>
/// <param name="Depth">Its depth in the subtask tree.</param>
/// <param name="Session">The session's name.</param>
/// <param name="State">The agent's state.</param>
/// <param name="Status">One of <see cref="WrapupStatus"/>.</param>
public sealed record WrapupRow(string Repo, string Worktree, string WorktreeId, string Path, string Branch, int Depth, string Session, string State, string Status)
{
    /// <summary>The git facts; null when the folder is gone.</summary>
    public GitFacts? Facts { get; init; }

    /// <summary>The linked PR.</summary>
    public WrapupPr? Pr { get; init; }

    /// <summary>Whole minutes in its state.</summary>
    public long? IdleMinutes { get; init; }

    /// <summary>The terminal to write to.</summary>
    public string? Terminal { get; init; }

    /// <summary>Its verdict, once answered.</summary>
    public WrapupVerdict? Verdict { get; init; }

    /// <summary>The waiting question, the reply's first line, or why a send failed.</summary>
    public string Note { get; init; } = "";

    /// <summary>Ids of the selected child worktrees, for the prompt and the roll-up.</summary>
    public IReadOnlyList<string> Children { get; init; } = [];

    /// <summary>The pane.</summary>
    public string Pane { get; init; } = "";

    /// <summary>The last ask recorded in the state file for this pane.</summary>
    public AskRecord? Asked { get; init; }

    /// <summary>The last verdict recorded in the state file for this pane.</summary>
    public AnswerRecord? Recorded { get; init; }

    /// <summary>The id sent in this run's prompt, for <c>estate wrapup report --ask ID</c>.</summary>
    public string AskId { get; init; } = "";
}

/// <summary>Reads git facts for one worktree; null when its folder is gone.</summary>
public delegate Task<GitFacts?> GitFactsReader(string path, CancellationToken cancellationToken);

/// <summary>
/// Finds Orca agent sessions that have gone quiet and asks each one whether its change is finished. Ported from
/// <c>src/rig/estate/wrapup.py</c>: read Orca, check the git facts first, ask the rest (docs/estate/wrapup.md).
/// </summary>
public static class Wrapup
{
    /// <summary>Prefixed to every wrap-up prompt. Orca keeps the first 200 characters of a prompt, so it must lead.</summary>
    public const string Marker = "[Wrap-up check]";

    /// <summary>How many sessions one run asks at once by default.</summary>
    public const int DefaultMaxSends = 5;

    /// <summary>How long <c>orca terminal send</c> waits to see the prompt submitted.</summary>
    public const int SubmitWaitSeconds = 10;

    /// <summary>The template's file name, beside <c>managed-projects.json</c>.</summary>
    public const string TemplateFile = "orca-wrapup.md";

    /// <summary>Ask only sessions idle this long by default.</summary>
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(30);

    /// <summary>The agents asked.</summary>
    public static readonly IReadOnlySet<string> Agents = new HashSet<string>(StringComparer.Ordinal) { "claude" };

    private static readonly Regex VerdictLine = new(
        @"^[^\w\n]*WRAPUP[^\w\n]*(?<kind>done|pending)\b[^\w\n]*(?<detail>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex Placeholder = new(
        @"\$(?:(?<escaped>\$)|(?<named>[_a-zA-Z][_a-zA-Z0-9]*)|\{(?<braced>[_a-zA-Z][_a-zA-Z0-9]*)\}|(?<invalid>))",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Rig's estate checks for one worktree; null when the folder is gone.</summary>
    public static async Task<GitFacts?> ReadGitFactsAsync(Git git, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        if (!Directory.Exists(path))
        {
            return null;
        }

        var merged = false;
        foreach (var @base in (string[])["origin/HEAD", "origin/main"])
        {
            if ((await git.RunAsync(path, ["merge-base", "--is-ancestor", "HEAD", @base], cancellationToken: cancellationToken).ConfigureAwait(false)).Ok)
            {
                merged = true;
                break;
            }
        }

        var dirty = (await git.RunAsync(path, ["status", "--porcelain"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty.Length > 0;
        var count = (await git.RunAsync(path, ["rev-list", "--count", "HEAD", "--not", "--remotes"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        var head = (await git.RunAsync(path, ["rev-parse", "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        return new GitFacts(dirty, count.Length > 0 && count.All(char.IsAsciiDigit) ? int.Parse(count, CultureInfo.InvariantCulture) : 0, merged, head);
    }

    /// <summary>Nothing left by the facts alone: clean, nothing unpushed, and the PR (or the branch) is merged.</summary>
    public static bool IsSettled(GitFacts? facts, WrapupPr? pr)
    {
        if (facts is null || facts.Dirty || facts.Unpushed != 0)
        {
            return false;
        }

        return pr is not null ? pr.State is "merged" or "closed" : facts.Merged;
    }

    /// <summary>One line a person, or the asked agent, can read.</summary>
    public static string DescribeFacts(GitFacts? facts, WrapupPr? pr)
    {
        List<string> parts = [];
        if (facts is null)
        {
            parts.Add("folder missing");
        }
        else
        {
            parts.Add(facts.Dirty ? "uncommitted changes" : "clean");
            if (facts.Unpushed != 0)
            {
                parts.Add($"{facts.Unpushed} commit(s) on no remote");
            }

            if (pr is null)
            {
                parts.Add(facts.Merged ? "nothing beyond the default branch" : "no PR");
            }
        }

        if (pr is not null)
        {
            var text = $"PR #{pr.Number} {(string.IsNullOrEmpty(pr.State) ? "?" : pr.State)}";
            if (!string.IsNullOrEmpty(pr.ChecksStatus) && pr.State is not ("merged" or "closed"))
            {
                text += $", checks {pr.ChecksStatus}";
            }

            parts.Add(text);
        }

        return string.Join("; ", parts);
    }

    /// <summary>Personal for the personal and org folders, work for the work folders, null otherwise.</summary>
    /// <param name="path">The folder.</param>
    /// <param name="personal">The personal and org folders (<see cref="EstateScopes.RepoParentDirs"/>).</param>
    /// <param name="work">This machine's work folders.</param>
    /// <param name="home">What <c>~</c> stands for.</param>
    public static WrapupScope? Classify(string path, IReadOnlyList<string> personal, IReadOnlyList<string> work, string home)
    {
        ArgumentNullException.ThrowIfNull(personal);
        ArgumentNullException.ThrowIfNull(work);
        var expanded = WrapupText.ExpandUser(path, home);
        if (personal.Any(parent => PathResolution.IsUnder(expanded, parent)))
        {
            return WrapupScope.Personal;
        }

        return work.Any(folder => PathResolution.IsUnder(expanded, folder)) ? WrapupScope.Work : null;
    }

    /// <summary>The worktrees in scope, in tree order: each parent followed by its children.</summary>
    public static IReadOnlyList<WrapupPlaced> Select(
        WrapupSnapshot snapshot, WrapupTarget target, IReadOnlyList<string> personal, IReadOnlyList<string> work, string home, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        var repoPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var repo in snapshot.Repos)
        {
            repoPaths[repo.Id] = repo.Path;
        }

        var inScope = snapshot.Worktrees.Where(w => Classify(repoPaths.GetValueOrDefault(w.RepoId, w.Path), personal, work, home) == target.Scope).ToList();
        var byId = new Dictionary<string, WrapupWorktree>(StringComparer.Ordinal);
        var kids = new Dictionary<string, List<WrapupWorktree>>(StringComparer.Ordinal);
        foreach (var w in inScope)
        {
            byId[w.WorktreeId] = w;
            if (!string.IsNullOrEmpty(w.ParentId))
            {
                if (!kids.TryGetValue(w.ParentId, out var list))
                {
                    kids[w.ParentId] = list = [];
                }

                list.Add(w);
            }
        }

        var wantedRepos = target.Repos.Select(WrapupText.Fold).ToHashSet(StringComparer.Ordinal);
        var chosen = inScope.Where(w => target.AllRepos || wantedRepos.Contains(WrapupText.Fold(w.Repo ?? ""))).ToList();
        if (target.Worktrees.Count > 0)
        {
            var pool = !(target.AllRepos || wantedRepos.Count > 0) ? inScope : chosen;
            chosen = [.. pool.Where(w => MatchesWorktree(w, target.Worktrees, home, workingDirectory))];
        }

        var selected = new HashSet<string>(chosen.Select(w => w.WorktreeId), StringComparer.Ordinal);
        if (target.Children)
        {
            var stack = new Stack<string>(selected);
            while (stack.Count > 0)
            {
                foreach (var child in kids.GetValueOrDefault(stack.Pop(), []))
                {
                    if (selected.Add(child.WorktreeId))
                    {
                        stack.Push(child.WorktreeId);
                    }
                }
            }
        }

        static string Key(WrapupWorktree w) => $"{WrapupText.Fold(w.Repo ?? "")}\0{WrapupText.Fold(w.Name)}";
        List<WrapupPlaced> placed = [];
        void Walk(WrapupWorktree w, int depth)
        {
            placed.Add(new(w, depth));
            foreach (var child in kids.GetValueOrDefault(w.WorktreeId, []).OrderBy(Key, StringComparer.Ordinal))
            {
                if (selected.Contains(child.WorktreeId))
                {
                    Walk(child, depth + 1);
                }
            }
        }

        var tops = inScope.Where(w => selected.Contains(w.WorktreeId) && byId[w.WorktreeId] == w
            && !(!string.IsNullOrEmpty(w.ParentId) && selected.Contains(w.ParentId)));
        foreach (var top in tops.OrderBy(Key, StringComparer.Ordinal))
        {
            Walk(top, 0);
        }

        return placed;
    }

    /// <summary>The first <c>WRAPUP: done</c> / <c>WRAPUP: pending: ...</c> line in a reply, or null when there isn't one.</summary>
    public static WrapupVerdict? ParseVerdict(string? message)
    {
        var found = VerdictLine.Match(message ?? "");
        if (!found.Success)
        {
            return null;
        }

        var detail = found.Groups["detail"].Value.Trim().TrimEnd('*', '`', ' ').Trim();
        return new WrapupVerdict(string.Equals(found.Groups["kind"].Value, "done", StringComparison.OrdinalIgnoreCase), detail);
    }

    /// <summary>One row per top-level agent of the chosen kinds, in tree order. Nothing is sent here.</summary>
    public static async Task<IReadOnlyList<WrapupRow>> PlanAsync(
        IReadOnlyList<WrapupPlaced> placed,
        IEnumerable<WrapupTerminal> terminals,
        DateTimeOffset now,
        WrapupOptions options,
        GitFactsReader facts,
        IWrapupStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(terminals);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(store);
        var byPane = new Dictionary<string, WrapupTerminal>(StringComparer.Ordinal);
        foreach (var terminal in terminals)
        {
            byPane[terminal.PaneKey] = terminal;
        }

        var selected = placed.Select(p => p.Worktree.WorktreeId).ToHashSet(StringComparer.Ordinal);
        List<WrapupRow> rows = [];
        foreach (var item in placed)
        {
            var w = item.Worktree;
            var agents = (w.Agents ?? []).Where(a => a.ParentPaneKey is null && a.AgentType is not null && options.Agents.Contains(a.AgentType)).ToList();
            if (agents.Count == 0)
            {
                continue;
            }

            var git = await facts(w.Path, cancellationToken).ConfigureAwait(false);
            var settled = IsSettled(git, w.LinkedPr);
            var saved = store.Read(w.Path) ?? WrapupState.Empty();
            string[] children = [.. placed.Where(p => p.Worktree.ParentId == w.WorktreeId && selected.Contains(p.Worktree.WorktreeId)).Select(p => p.Worktree.WorktreeId)];
            foreach (var agent in agents)
            {
                var terminal = byPane.GetValueOrDefault(agent.PaneKey);
                var idle = Idle(agent, now);
                var ask = saved.LastAsk(agent.PaneKey);
                var recorded = saved.LastAnswer(agent.PaneKey);
                var answer = ask is not null ? saved.AnswerTo(ask) : null;
                var status = Status(agent, terminal, idle, settled, options, ask, answered: answer is not null);
                var verdict = status == WrapupStatus.Answered ? VerdictOf(agent, answer) : null;
                string note;
                if (status == WrapupStatus.Waiting)
                {
                    note = WaitingNote(agent);
                }
                else if (status == WrapupStatus.Answered && verdict is null)
                {
                    note = "replied without a WRAPUP line: " + WrapupText.FirstLine(agent.LastMessage);
                }
                else if (status == WrapupStatus.Unconfirmed && ask is not null)
                {
                    note = $"asked {WrapupText.Head(ask.At, 16)}Z, but the pane has not moved since: check it";
                }
                else if (status != WrapupStatus.Answered && recorded is not null)
                {
                    note = $"last answer {WrapupText.Head(recorded.At, 16)}Z: {recorded.Verdict}" + (recorded.Detail.Length > 0 ? $": {recorded.Detail}" : "");
                }
                else
                {
                    note = "";
                }

                rows.Add(new WrapupRow(
                    w.Repo ?? "",
                    w.Name,
                    w.WorktreeId,
                    w.Path,
                    w.BranchName,
                    item.Depth,
                    string.IsNullOrEmpty(agent.ConversationName) ? WrapupText.FirstLine(agent.Prompt, 60) : agent.ConversationName,
                    agent.State,
                    status)
                {
                    Facts = git,
                    Pr = w.LinkedPr,
                    IdleMinutes = idle is { } span ? (long)Math.Floor(span.TotalSeconds / 60) : null,
                    Terminal = terminal?.Handle,
                    Verdict = verdict,
                    Note = WrapupText.FirstLine(note, 240),
                    Children = children,
                    Pane = agent.PaneKey,
                    Asked = ask,
                    Recorded = recorded,
                });
            }
        }

        return Cap(rows, options.MaxSends);
    }

    /// <summary>Indexes of the rows to ask, deepest subtasks first.</summary>
    public static IReadOnlyList<int> SendOrder(IReadOnlyList<WrapupRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return [.. Enumerable.Range(0, rows.Count).Where(i => rows[i].Status == WrapupStatus.Ask).OrderBy(i => -rows[i].Depth).ThenBy(i => i)];
    }

    /// <summary>The template file beside <paramref name="managedProjectsFile"/>.</summary>
    public static string TemplatePath(string managedProjectsFile) => Path.Join(Path.GetDirectoryName(managedProjectsFile), TemplateFile);

    /// <summary>The template behind the marker, with <c>$repo</c>, <c>$worktree</c>, <c>$branch</c>, <c>$facts</c>, <c>$children</c>, <c>$report</c>.</summary>
    public static string RenderPrompt(string template, WrapupRow row, IReadOnlyList<WrapupRow> rows, IReadOnlyDictionary<string, WrapupWorktree> worktrees, string rigExecutable)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(worktrees);
        var byWorktree = new Dictionary<string, List<WrapupRow>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!byWorktree.TryGetValue(r.WorktreeId, out var list))
            {
                byWorktree[r.WorktreeId] = list = [];
            }

            list.Add(r);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["repo"] = row.Repo,
            ["worktree"] = row.Worktree,
            ["branch"] = string.IsNullOrEmpty(row.Branch) ? "(detached)" : row.Branch,
            ["facts"] = DescribeFacts(row.Facts, row.Pr),
            ["children"] = ChildSummary(row, byWorktree, worktrees),
            ["report"] = ReportCommand(row.AskId, rigExecutable),
        };
        return $"{Marker} {SafeSubstitute(template, values).Trim()}";
    }

    /// <summary>Python's <c>string.Template.safe_substitute</c>: unknown and malformed placeholders stay as written.</summary>
    public static string SafeSubstitute(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Placeholder.Replace(template, m =>
        {
            if (m.Groups["escaped"].Success)
            {
                return "$";
            }

            var name = m.Groups["named"].Success ? m.Groups["named"].Value : m.Groups["braced"].Success ? m.Groups["braced"].Value : null;
            return name is not null && values.TryGetValue(name, out var value) ? value : m.Value;
        });
    }

    /// <summary>The command line the prompt hands the agent, shell-quoted (the runtime copy's path has a space).</summary>
    public static string ReportCommand(string askId, string rigExecutable)
    {
        List<string> parts = [rigExecutable, "estate", "wrapup", "report"];
        if (!string.IsNullOrEmpty(askId))
        {
            parts.AddRange(["--ask", askId]);
        }

        return string.Join(' ', parts.Select(WrapupText.ShellQuote));
    }

    /// <summary>Selected descendant sessions not yet known to be finished (settled or answered done).</summary>
    public static int OpenChildren(WrapupRow row, IReadOnlyList<WrapupRow> rows)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rows);
        var byParent = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            _ = byParent.TryAdd(r.WorktreeId, r.Children);
        }

        var pending = 0;
        var stack = new List<string>(row.Children);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            var id = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (!seen.Add(id))
            {
                continue;
            }

            foreach (var r in rows.Where(r => r.WorktreeId == id))
            {
                var finished = r.Status == WrapupStatus.Settled || r.Verdict is { Done: true };
                pending += finished ? 0 : 1;
            }

            stack.AddRange(byParent.GetValueOrDefault(id, []));
        }

        return pending;
    }

    /// <summary>What a waiting agent is waiting on.</summary>
    internal static string WaitingNote(WrapupAgent agent)
    {
        var message = agent.ProgramStatus?.Message;
        return !string.IsNullOrEmpty(message) ? message : !string.IsNullOrEmpty(agent.ToolName) ? $"waiting on {agent.ToolName}" : "";
    }

    private static TimeSpan? Idle(WrapupAgent agent, DateTimeOffset now) =>
        agent.StateStartedAt is { } started ? now - DateTimeOffset.UnixEpoch.AddTicks((long)(started * TimeSpan.TicksPerMillisecond)) : null;

    /// <summary>Whether the pane changed state after <paramref name="ask"/>: the prompt arrived, or its owner carried on.</summary>
    private static bool MovedSince(WrapupAgent agent, AskRecord ask)
    {
        var askedAt = WrapupStamps.Parse(ask.At);
        if (askedAt is null || agent.StateStartedAt is null)
        {
            return true;
        }

        return agent.StateStartedAt.Value / 1000 > askedAt.Value.ToUnixTimeMilliseconds() / 1000.0;
    }

    private static string Status(WrapupAgent agent, WrapupTerminal? terminal, TimeSpan? idle, bool settled, WrapupOptions options, AskRecord? ask, bool answered)
    {
        var asked = (agent.Prompt ?? "").TrimStart().StartsWith(Marker, StringComparison.Ordinal);
        if (asked && answered)
        {
            // The agent recorded its verdict (`estate wrapup report`), even if it is still finishing its turn.
            return WrapupStatus.Answered;
        }

        if (agent.State == "working")
        {
            return asked ? WrapupStatus.Answering : WrapupStatus.Working;
        }

        if (agent.State == "waiting")
        {
            return WrapupStatus.Waiting;
        }

        if (asked)
        {
            return WrapupStatus.Answered;
        }

        if (ask is not null && !answered && !MovedSince(agent, ask))
        {
            return WrapupStatus.Unconfirmed;
        }

        if (terminal is null || terminal.Writable != true || terminal.AgentIdentity != agent.AgentType)
        {
            return WrapupStatus.NoTerminal;
        }

        if (idle is null || idle < options.Idle)
        {
            return WrapupStatus.Recent;
        }

        return settled && !options.IncludeSettled ? WrapupStatus.Settled : WrapupStatus.Ask;
    }

    private static WrapupVerdict? VerdictOf(WrapupAgent agent, AnswerRecord? answer) =>
        answer is not null ? new WrapupVerdict(answer.Verdict == "done", answer.Detail) : ParseVerdict(agent.LastMessage);

    /// <summary>Defers asks past --max; the deepest subtasks go first, so children answer before their parents.</summary>
    private static List<WrapupRow> Cap(List<WrapupRow> rows, int? maxSends)
    {
        if (maxSends is not { } max)
        {
            return rows;
        }

        var deferred = SendOrder(rows).Skip(Math.Max(max, 0)).ToHashSet();
        return [.. rows.Select((r, i) => deferred.Contains(i) ? r with { Status = WrapupStatus.Deferred } : r)];
    }

    private static string ChildSummary(WrapupRow row, Dictionary<string, List<WrapupRow>> rowsByWorktree, IReadOnlyDictionary<string, WrapupWorktree> worktrees)
    {
        if (row.Children.Count == 0)
        {
            return "none";
        }

        List<string> lines = [];
        foreach (var childId in row.Children)
        {
            if (!worktrees.TryGetValue(childId, out var child))
            {
                continue;
            }

            var childRows = rowsByWorktree.GetValueOrDefault(childId, []);
            var verdicts = childRows.Where(r => r.Verdict is not null).Select(r => r.Verdict!).ToList();
            string said;
            if (verdicts.Count > 0)
            {
                said = verdicts.All(v => v.Done) ? "done" : "pending: " + string.Join("; ", verdicts.Where(v => !v.Done).Select(v => v.Detail));
            }
            else if (childRows.Count > 0)
            {
                said = string.Join(", ", childRows.Select(r => r.Status).Distinct().Order(StringComparer.Ordinal));
            }
            else
            {
                said = "no agent";
            }

            var facts = DescribeFacts(childRows.Count > 0 ? childRows[0].Facts : null, child.LinkedPr);
            lines.Add($"{child.Repo ?? "None"}/{child.Name} ({child.BranchName}; {facts}; {said})");
        }

        return lines.Count > 0 ? string.Join("; ", lines) : "none";
    }

    private static bool MatchesWorktree(WrapupWorktree worktree, IReadOnlyList<string> wanted, string home, string workingDirectory)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { WrapupText.Fold(worktree.Name), WrapupText.Fold(WrapupText.BaseName(worktree.Path)), WrapupText.Fold(worktree.BranchName) };
        foreach (var want in wanted)
        {
            if (names.Contains(WrapupText.Fold(want)))
            {
                return true;
            }

            var candidate = WrapupText.ExpandUser(want, home);
            if (Path.IsPathRooted(candidate)
                && PathResolution.Same(Path.GetFullPath(candidate, workingDirectory), Path.GetFullPath(worktree.Path, workingDirectory)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Reads Orca through its CLI, as <c>wrapup.py</c> did: <c>orca worktree ps</c>, <c>terminal list</c>, <c>repo list</c> and <c>terminal send</c>.</summary>
/// <param name="proc">Runs <c>orca</c>.</param>
public sealed class WrapupOrca(IProc proc)
{
    private const string PsLimit = "1000";
    private const string Unreadable = "orca answered in a shape this version of rig does not read";
    private static readonly TimeSpan OrcaTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Only <c>orca worktree ps</c>: what a wait loop polls between sends.</summary>
    /// <exception cref="WrapupException">Orca could not answer.</exception>
    public async Task<IReadOnlyList<WrapupWorktree>> ReadWorktreesAsync(CancellationToken cancellationToken = default)
    {
        var ps = Envelope(await CallAsync(["worktree", "ps", "--limit", PsLimit, "--json"], cancellationToken).ConfigureAwait(false));
        return (ps is null ? null : WrapupWire.Worktrees(ps)) ?? throw new WrapupException(Unreadable);
    }

    /// <summary>Worktrees with their agents, live terminals and registered repos.</summary>
    /// <exception cref="WrapupException">Orca could not answer.</exception>
    public async Task<WrapupSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var ps = Envelope(await CallAsync(["worktree", "ps", "--limit", PsLimit, "--json"], cancellationToken).ConfigureAwait(false));
        var terminals = Envelope(await CallAsync(["terminal", "list", "--json"], cancellationToken).ConfigureAwait(false));
        var repos = Envelope(await CallAsync(["repo", "list", "--json"], cancellationToken).ConfigureAwait(false));
        var worktrees = ps is null ? null : WrapupWire.Worktrees(ps);
        var live = terminals is null ? null : WrapupWire.Terminals(terminals);
        if (worktrees is null || live is null)
        {
            throw new WrapupException(Unreadable);
        }

        return new WrapupSnapshot(worktrees, live, (repos is null ? null : WrapupWire.Repos(repos)) ?? [], WrapupWire.Truncated(ps!));
    }

    /// <summary>Pastes <paramref name="text"/> into an agent terminal and presses Enter, waiting briefly to see it submitted. Null when sent, else why not.</summary>
    public async Task<string?> SendAsync(string handle, string text, CancellationToken cancellationToken = default)
    {
        var ran = await RunAsync(
            ["terminal", "send", "--terminal", handle, "--text", text, "--enter", "--wait-submit", Wrapup.SubmitWaitSeconds.ToString(CultureInfo.InvariantCulture), "--json"],
            cancellationToken).ConfigureAwait(false);
        var (decoded, ok, message) = ran.Out.Trim().Length > 0 ? WrapupWire.Receipt(ran.Out) : (false, false, null);
        if (ran.Ok && (!decoded || ok))
        {
            return null;
        }

        return !string.IsNullOrEmpty(message) ? message
            : WrapupText.FirstLine(ran.Err.Length > 0 ? ran.Err : ran.Out) is { Length: > 0 } line ? line
            : $"exit {ran.Code}";
    }

    private static JsonObject? Envelope(string text)
    {
        try
        {
            var root = OrcaJson.Object(text);
            _ = OrcaJson.Bool(root, "ok");
            return root;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private Task<Ran> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        proc.RunAsync(new ProcRequest(["orca", .. args]) { Timeout = OrcaTimeout }, cancellationToken);

    private async Task<string> CallAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var ran = await RunAsync(args, cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            var detail = WrapupText.SplitLines((ran.Err.Length > 0 ? ran.Err : ran.Out).Trim());
            throw new WrapupException($"orca {args[0]} {args[1]} failed: {(detail.Count > 0 ? detail[0] : $"exit {ran.Code}")}");
        }

        return ran.Out;
    }
}

/// <summary>Decodes Orca's answers as pydantic did: a row missing a required field is dropped, an optional field of the wrong type is null.</summary>
internal static class WrapupWire
{
    public static List<WrapupWorktree>? Worktrees(JsonObject envelope) => Rows(envelope, "worktrees", Worktree);

    public static List<WrapupTerminal>? Terminals(JsonObject envelope) => Rows(envelope, "terminals", Terminal);

    public static List<WrapupRepo>? Repos(JsonObject envelope) => Rows(envelope, "repos", Repo);

    public static bool Truncated(JsonObject envelope) =>
        Result(envelope) is { } result && Lenient(() => OrcaJson.Bool(result, "truncated")) == true;

    public static (bool Decoded, bool Ok, string? Message) Receipt(string text)
    {
        try
        {
            var root = OrcaJson.Object(text);
            var ok = OrcaJson.Bool(root, "ok") ?? false;
            var error = OrcaJson.Lenient(() => OrcaJson.Object(root, "error"));
            return (true, ok, error is null ? null : OrcaJson.Lenient(() => OrcaJson.String(error, "message")));
        }
        catch (InvalidDataException)
        {
            return (false, false, null);
        }
    }

    private static T? Lenient<T>(Func<T?> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static JsonObject? Result(JsonObject envelope) => OrcaJson.Lenient(() => OrcaJson.Object(envelope, "result"));

    private static List<T>? Rows<T>(JsonObject envelope, string key, Func<JsonObject, T> read)
        where T : class
    {
        if (Result(envelope) is not { } result)
        {
            return null;
        }

        if (!result.TryGetPropertyValue(key, out var node))
        {
            return [];
        }

        if (node is not JsonArray items)
        {
            return null;
        }

        return [.. items.Select(item => item is JsonObject row ? OrcaJson.Lenient(() => read(row)) : null).OfType<T>()];
    }

    private static string Required(JsonObject row, string key) => OrcaJson.String(row, key) ?? throw new InvalidDataException($"{key}: required");

    private static string? Text(JsonObject row, string key) => OrcaJson.Lenient(() => OrcaJson.String(row, key));

    private static WrapupWorktree Worktree(JsonObject row) => new(Required(row, "worktreeId"), Required(row, "repoId"), Required(row, "path"))
    {
        Repo = Text(row, "repo"),
        DisplayName = Text(row, "displayName"),
        Branch = Text(row, "branch"),
        ParentId = Text(row, "parentWorktreeId"),
        LinkedPr = OrcaJson.Lenient(() => OrcaJson.Object(row, "linkedPR") is { } pr ? Pr(pr) : null),
        Agents = OrcaJson.Lenient(() => OrcaJson.Array(row, "agents") is { } agents
            ? (IReadOnlyList<WrapupAgent>)[.. agents.Select(a => a is JsonObject agent ? OrcaJson.Lenient(() => Agent(agent)) : null).OfType<WrapupAgent>()]
            : null),
    };

    private static WrapupPr Pr(JsonObject pr)
    {
        if (!pr.TryGetPropertyValue("number", out var node) || node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !value.TryGetValue<long>(out var number))
        {
            throw new InvalidDataException("number: expected a whole number");
        }

        return new(number, Text(pr, "state"), Text(pr, "checksStatus"));
    }

    private static WrapupAgent Agent(JsonObject row) => new(Required(row, "paneKey"), Required(row, "state"))
    {
        AgentType = Text(row, "agentType"),
        ParentPaneKey = Text(row, "parentPaneKey"),
        Prompt = Text(row, "prompt"),
        ConversationName = Text(row, "conversationName"),
        LastMessage = Text(row, "lastAssistantMessage"),
        ToolName = Text(row, "toolName"),
        StateStartedAt = row.TryGetPropertyValue("stateStartedAt", out var started) && started is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            ? value.GetValue<double>()
            : null,
        ProgramStatus = OrcaJson.Lenient(() => OrcaJson.Object(row, "programStatus") is { } status
            ? new WrapupProgramStatus(Text(status, "state"), Text(status, "kind"), Text(status, "message"))
            : null),
    };

    private static WrapupTerminal Terminal(JsonObject row) => new(
        Required(row, "handle"),
        Text(row, "tabId"),
        Text(row, "leafId"),
        Text(row, "agentIdentity"),
        Lenient(() => OrcaJson.Bool(row, "writable")));

    private static WrapupRepo Repo(JsonObject row) => new(Required(row, "id"), Required(row, "path"), OrcaJson.String(row, "displayName") ?? (row.ContainsKey("displayName") ? throw new InvalidDataException("displayName") : ""));
}

/// <summary>Python's string helpers that wrap-up's output depends on.</summary>
public static class WrapupText
{
    private static readonly Regex SafeWord = new(@"^[\w@%+=:,./-]+$", RegexOptions.CultureInvariant | RegexOptions.ECMAScript, TimeSpan.FromSeconds(1));

    /// <summary>The first non-blank line, stripped, cut to <paramref name="limit"/> characters with an ellipsis.</summary>
    public static string FirstLine(string? text, int limit = 160)
    {
        var line = SplitLines(text ?? "").Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= limit ? line : line[..(limit - 1)] + "…";
    }

    /// <summary>Python's <c>str.splitlines()</c>.</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> lines = [];
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>Python's <c>shlex.quote</c>.</summary>
    public static string ShellQuote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return "''";
        }

        return SafeWord.IsMatch(text) ? text : "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    /// <summary>The first <paramref name="length"/> characters.</summary>
    public static string Head(string text, int length) => text is null ? "" : text.Length <= length ? text : text[..length];

    /// <summary>Close to Python's <c>str.casefold()</c>.</summary>
    public static string Fold(string text) => (text ?? "").ToLowerInvariant();

    /// <summary>The last path segment, as <c>Path(path).name</c>.</summary>
    public static string BaseName(string path) => Path.GetFileName((path ?? "").TrimEnd('/', Path.DirectorySeparatorChar));

    /// <summary><c>~</c> and <c>~/…</c> under <paramref name="home"/>.</summary>
    public static string ExpandUser(string path, string home)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path == "~")
        {
            return home;
        }

        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? Path.Join(home, path[2..])
            : path;
    }
}
