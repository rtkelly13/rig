using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Rig.Core.Orca;

/// <summary>One tab Orca lost across a restart.</summary>
/// <param name="Title">The tab's label.</param>
/// <param name="TabWorktree">The folder of the worktree the tab sat in.</param>
/// <param name="Agent">The agent it was running, if any.</param>
/// <param name="SessionId">The agent session's id, if any.</param>
/// <param name="SessionCwd">The folder the agent session ran in, if recorded.</param>
/// <param name="TranscriptExists">Whether the session's transcript is still on disk; null when none is recorded.</param>
/// <param name="Command">The <c>orca terminal create</c> line that reopens the tab; null for a plain shell or an unknown agent.</param>
public sealed record LostTab(string Title, string TabWorktree, string? Agent, string? SessionId, string? SessionCwd, bool? TranscriptExists, string? Command);

/// <summary>What was lost, and what it was compared against.</summary>
/// <param name="OrcaData">The user-data folder read.</param>
/// <param name="Before">The backup the live state was compared with.</param>
/// <param name="BeforeSavedMs">When that backup's session document was saved, in Unix milliseconds.</param>
/// <param name="Lost">The tabs in the backup that are gone from the live state.</param>
public sealed record LostTabsReport(string OrcaData, string Before, long BeforeSavedMs, IReadOnlyList<LostTab> Lost)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The report as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The report as the human-readable list.</summary>
    public string ToText()
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Orca data: {OrcaData}\nCompared from: {Path.GetFileName(Before)}\n\n"));
        if (Lost.Count == 0)
        {
            _ = text.Append("No tabs lost.\n");
        }

        foreach (var tab in Lost)
        {
            var what = tab.SessionId is null ? "plain shell" : $"{tab.Agent} {tab.SessionId}";
            _ = text.Append(CultureInfo.InvariantCulture, $"- {tab.Title}  [{tab.TabWorktree}]  {what}").Append(tab.TranscriptExists == false ? "  (transcript missing)" : string.Empty).Append('\n');
            if (tab.Command is not null)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"    {tab.Command}\n");
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// Lists the agent tabs an Orca instance lost across a restart. Orca keeps its tabs in the <c>workspaceSession</c> document of
/// <c>profiles/local-default/profile-state.db</c> and writes timestamped <c>profile-state.db.backup.*.db</c> copies beside it;
/// this compares the newest backup saved before the running Orca started with the live state. Read-only: each database is
/// copied through SQLite's backup API into memory, so a checkpoint mid-read cannot drop tabs and nothing is written.
/// </summary>
public static class OrcaLostTabs
{
    private static readonly Dictionary<string, string> Resume = new(StringComparer.Ordinal)
    {
        ["claude"] = "claude --resume {0}",
        ["opencode"] = "opencode --auto -s {0}",
        ["agy"] = "agy --conversation {0} --dangerously-skip-permissions",
    };

    /// <summary>Compares the backup (default: the newest saved before Orca started) with the live state of the instance.</summary>
    /// <param name="locations">Where Orca keeps its files on this machine.</param>
    /// <param name="orcaData">The user-data folder, or null for <c>ORCA_USER_DATA_PATH</c> or the platform default.</param>
    /// <param name="before">The backup to compare from, or null to pick the newest one from before the running Orca started.</param>
    /// <exception cref="InvalidOperationException">A database has no session document, or no backup predates this Orca.</exception>
    public static LostTabsReport Find(OrcaLocations locations, string? orcaData, string? before)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var data = locations.UserData(orcaData) ?? locations.DefaultUserData();
        var profile = locations.ProfileDir(data);
        var backup = before ?? PickBefore(profile, StartedAt(locations.RuntimeMetadata(data)));
        var (then, savedMs) = ReadSession(backup);
        var (now, _) = ReadSession(Path.Join(profile, OrcaProfile.LiveDb));
        return new LostTabsReport(data, backup, savedMs, Diff(then, now));
    }

    /// <summary>The tabs in <paramref name="before"/> that <paramref name="after"/> no longer has open, with the commands that reopen them.</summary>
    public static IReadOnlyList<LostTab> Diff(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var open = Tabs(after).Select(t => t.Tab).ToList();
        var stillOpen = open.Select(t => Text(t["id"])).ToHashSet(StringComparer.Ordinal);
        var reopened = open.Select(t => Text((t["aiVaultTitle"] as JsonObject)?["sessionId"])).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var asleep = (before["sleepingAgentSessionsByPaneKey"] as JsonObject ?? new JsonObject()).Select(p => p.Value as JsonObject).OfType<JsonObject>()
            .GroupBy(s => Text(s["tabId"])).ToDictionary(g => g.Key ?? string.Empty, g => g.Last(), StringComparer.Ordinal);

        var lost = new List<LostTab>();
        foreach (var (worktree, tab) in Tabs(before))
        {
            var id = Text(tab["id"]);
            if (stillOpen.Contains(id))
            {
                continue;
            }

            var vault = tab["aiVaultTitle"] as JsonObject;
            var sleep = asleep.GetValueOrDefault(id ?? string.Empty);
            var provider = sleep?["providerSession"] as JsonObject;
            var agent = Text(vault?["agent"]) ?? Text(sleep?["agent"]);
            var session = Text(vault?["sessionId"]) ?? Text(provider?["id"]);
            if (session is not null && reopened.Contains(session))
            {
                continue;
            }

            var dir = worktree[(worktree.IndexOf("::", StringComparison.Ordinal) is var at and >= 0 ? at + 2 : 0)..];
            var cwd = Text(provider?["cwd"])?.Replace('\\', '/');
            var label = Text(tab["label"]) ?? string.Empty;
            var transcript = Text(provider?["transcriptPath"]);
            lost.Add(new LostTab(
                label,
                dir,
                agent,
                session,
                cwd,
                transcript is null ? null : File.Exists(transcript) || Directory.Exists(transcript),
                agent is not null && session is not null && Resume.TryGetValue(agent, out var line) ? Reopen(dir, label, cwd, string.Format(null, line, session)) : null));
        }

        return lost;
    }

    /// <summary>The <c>workspaceSession</c> document of <paramref name="db"/> and when it was saved (Unix milliseconds), read from one consistent snapshot.</summary>
    /// <exception cref="InvalidOperationException">The database has no such document.</exception>
    public static (JsonObject Session, long UpdatedMs) ReadSession(string db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!File.Exists(db))
        {
            throw new InvalidOperationException($"{db}: no such database");
        }

        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        using var copy = new SqliteConnection("Data Source=:memory:");
        source.Open();
        copy.Open();
        source.BackupDatabase(copy);
        using var query = copy.CreateCommand();
        query.CommandText = "select payload, updated_at from profile_state_documents where domain = 'workspaceSession'";
        using var rows = query.ExecuteReader();
        return rows.Read()
            ? (OrcaJson.Object(rows.GetString(0), $"{db} workspaceSession"), rows.GetInt64(1))
            : throw new InvalidOperationException($"{db}: no workspaceSession document");
    }

    private static string PickBefore(string profile, double? startedMs)
    {
        var backups = (Directory.Exists(profile) ? Directory.EnumerateFiles(profile, "profile-state.db.backup.*.db") : [])
            .Select(path => (Path: path, Saved: ReadSession(path).UpdatedMs))
            .Where(b => startedMs is not { } started || b.Saved < started)
            .ToList();
        return backups.Count == 0
            ? throw new InvalidOperationException(
                $"{profile}: no backup saved before this Orca started. Orca keeps only its five newest hourly backups, so it has rotated away; pass --before, or fall back to daemon.log.")
            : backups.OrderByDescending(b => b.Saved).ThenByDescending(b => b.Path, StringComparer.Ordinal).First().Path;
    }

    private static double? StartedAt(string runtime) =>
        File.Exists(runtime) && JsonNode.Parse(File.ReadAllText(runtime)) is JsonObject doc && doc["startedAt"] is JsonValue value && value.TryGetValue(out double ms) ? ms : null;

    private static IEnumerable<(string Worktree, JsonObject Tab)> Tabs(JsonObject session) =>
        (session["unifiedTabs"] as JsonObject ?? new JsonObject()).SelectMany(w => (w.Value as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(tab => (w.Key, tab)));

    private static string? Text(JsonNode? node) => node is JsonValue value && value.ToString() is { Length: > 0 } text ? text : null;

    private static string Reopen(string dir, string label, string? cwd, string resume)
    {
        var inner = cwd is null || SameDir(cwd, dir) ? resume : $"cd '{cwd.Replace("'", "''", StringComparison.Ordinal)}'; {resume}";
        return $"orca terminal create --worktree {ShellQuote("path:" + dir)} --title {ShellQuote(label)} --command {ShellQuote(inner)} --json";
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // POSIX shell quoting, as Python's shlex.quote does it: bare when every character is safe, else single-quoted.
    private static string ShellQuote(string text) =>
        text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || "_@%+=:,./-".Contains(c, StringComparison.Ordinal))
            ? text
            : "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
