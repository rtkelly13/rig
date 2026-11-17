using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Rig.Core.Orca;

namespace Rig.Core.Tests.Orca;

/// <summary><c>workspace orca lost-tabs</c>: which tabs a restart dropped and how to reopen them. Ported from <c>tools/orca-lost-tabs</c>.</summary>
public sealed class OrcaLostTabsTests : IDisposable
{
    private const string Before = """
        {"unifiedTabs": {"repo::/wt/app": [
           {"id": "t1", "label": "build", "aiVaultTitle": {"agent": "claude", "sessionId": "s1"}},
           {"id": "t2", "label": "kept"},
           {"id": "t3", "label": "moved", "aiVaultTitle": {"agent": "claude", "sessionId": "s3"}},
           {"id": "t4", "label": "reopened", "aiVaultTitle": {"agent": "opencode", "sessionId": "s4"}},
           {"id": "t5", "label": "shell"},
           {"id": "t6", "label": "it's", "aiVaultTitle": {"agent": "agy", "sessionId": "s6"}}]},
         "sleepingAgentSessionsByPaneKey": {
           "p1": {"tabId": "t1", "providerSession": {"id": "s1", "cwd": "/wt/app"}},
           "p3": {"tabId": "t3", "providerSession": {"id": "s3", "cwd": "C:\\other dir", "transcriptPath": "/no/such/transcript.jsonl"}}}}
        """;

    private const string After = """
        {"unifiedTabs": {"repo::/wt/app": [
           {"id": "t2", "label": "kept"},
           {"id": "n4", "label": "again", "aiVaultTitle": {"agent": "opencode", "sessionId": "s4"}}]}}
        """;

    private readonly string _root = RepoTemp.NewPath("lost-tabs");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static JsonObject Doc(string json) => (JsonObject)JsonNode.Parse(json)!;

    private OrcaLocations Locations(string? variable = null) =>
        new(name => name == OrcaLocations.UserDataVariable ? variable : null, Path.Join(_root, "home"), OrcaOs.Linux);

    private string Profile => Path.Join(_root, "orca", "profiles", "local-default");

    [Fact]
    public void Lost_tabs_skip_open_tabs_and_sessions_already_reopened_and_reopen_the_rest()
    {
        var lost = OrcaLostTabs.Diff(Doc(Before), Doc(After));

        Assert.Equal(["build", "moved", "shell", "it's"], lost.Select(t => t.Title));
        Assert.Equal(
            "orca terminal create --worktree path:/wt/app --title build --command 'claude --resume s1' --json",
            lost[0].Command);
        Assert.Equal(
            "orca terminal create --worktree path:/wt/app --title moved --command 'cd '\"'\"'C:/other dir'\"'\"'; claude --resume s3' --json",
            lost[1].Command);
        Assert.Equal(("C:/other dir", false), (lost[1].SessionCwd, lost[1].TranscriptExists));
        Assert.Equal((null, null, null), (lost[2].Agent, lost[2].Command, lost[2].TranscriptExists));
        Assert.Contains("--title 'it'\"'\"'s' --command 'agy --conversation s6 --dangerously-skip-permissions'", lost[3].Command, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transcript_that_is_still_on_disk_is_reported_as_existing()
    {
        _ = Directory.CreateDirectory(_root);
        var transcript = Path.Join(_root, "s.jsonl");
        File.WriteAllText(transcript, "{}");
        var before = new JsonObject
        {
            ["unifiedTabs"] = new JsonObject { ["w::/w"] = new JsonArray(new JsonObject { ["id"] = "a", ["label"] = "x" }) },
            ["sleepingAgentSessionsByPaneKey"] = new JsonObject { ["p"] = new JsonObject { ["tabId"] = "a", ["agent"] = "claude", ["providerSession"] = new JsonObject { ["id"] = "s", ["transcriptPath"] = transcript } } },
        };

        var tab = Assert.Single(OrcaLostTabs.Diff(before, Doc("{}")));

        Assert.Equal(("claude", "s", true), (tab.Agent, tab.SessionId, tab.TranscriptExists));
    }

    [Fact]
    public void The_newest_backup_from_before_Orca_started_is_compared_and_nothing_is_written()
    {
        var older = SessionDb.Write(Path.Join(Profile, "profile-state.db.backup.1.db"), """{"unifiedTabs": {}}""", 1_000);
        _ = SessionDb.Write(Path.Join(Profile, "profile-state.db.backup.2.db"), Before, 2_000);
        _ = SessionDb.Write(Path.Join(Profile, "profile-state.db.backup.3.db"), """{"unifiedTabs": {}}""", 9_000);
        var live = SessionDb.Write(Path.Join(Profile, OrcaProfile.LiveDb), After, 5_000);
        File.WriteAllText(Path.Join(_root, "orca", OrcaLocations.RuntimeFile), """{"startedAt": 5000}""");
        var untouched = Fingerprint(older, live);

        var report = OrcaLostTabs.Find(Locations(), Path.Join(_root, "orca"), before: null);

        Assert.Equal(("profile-state.db.backup.2.db", 2_000, 4), (Path.GetFileName(report.Before), report.BeforeSavedMs, report.Lost.Count));
        Assert.Equal(untouched, Fingerprint(older, live));
    }

    [Fact]
    public void Without_a_start_time_the_newest_backup_wins_and_the_data_folder_comes_from_the_environment()
    {
        _ = SessionDb.Write(Path.Join(Profile, "profile-state.db.backup.1.db"), Before, 1_000);
        _ = SessionDb.Write(Path.Join(Profile, "profile-state.db.backup.2.db"), """{"unifiedTabs": {}}""", 2_000);
        _ = SessionDb.Write(Path.Join(Profile, OrcaProfile.LiveDb), After, 5_000);

        var report = OrcaLostTabs.Find(Locations(Path.Join(_root, "orca")), orcaData: null, before: null);

        Assert.Equal((Path.Join(_root, "orca"), "profile-state.db.backup.2.db", 0), (report.OrcaData, Path.GetFileName(report.Before), report.Lost.Count));
    }

    [Fact]
    public void An_explicit_backup_overrides_the_pick()
    {
        var chosen = SessionDb.Write(Path.Join(_root, "mine.db"), Before, 1_000);
        _ = SessionDb.Write(Path.Join(Profile, OrcaProfile.LiveDb), After, 5_000);

        var report = OrcaLostTabs.Find(Locations(), Path.Join(_root, "orca"), chosen);

        Assert.Equal((chosen, 4), (report.Before, report.Lost.Count));
    }

    [Fact]
    public void No_backup_from_before_the_start_is_an_error_that_names_the_fix()
    {
        _ = SessionDb.Write(Path.Join(Profile, "profile-state.db.backup.1.db"), Before, 9_000);
        File.WriteAllText(Path.Join(_root, "orca", OrcaLocations.RuntimeFile), """{"startedAt": 5000}""");

        var error = Assert.Throws<InvalidOperationException>(() => OrcaLostTabs.Find(Locations(), Path.Join(_root, "orca"), before: null));

        Assert.Contains("pass --before", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_without_a_session_document_is_an_error()
    {
        var path = SessionDb.Write(Path.Join(_root, "x.db"), "{}", 1);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "delete from profile_state_documents";
            _ = command.ExecuteNonQuery();
        }

        Assert.Contains("no workspaceSession document", Assert.Throws<InvalidOperationException>(() => OrcaLostTabs.ReadSession(path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_renders_as_text_and_as_snake_case_JSON()
    {
        var report = new LostTabsReport("/orca", "/orca/b.db", 7, OrcaLostTabs.Diff(Doc(Before), Doc(After)));

        var text = report.ToText();
        var json = JsonNode.Parse(report.ToJson())!;

        Assert.StartsWith("Orca data: /orca\nCompared from: b.db\n\n- build  [/wt/app]  claude s1\n    orca terminal create", text, StringComparison.Ordinal);
        Assert.Contains("- moved  [/wt/app]  claude s3  (transcript missing)\n", text, StringComparison.Ordinal);
        Assert.Contains("- shell  [/wt/app]  plain shell\n", text, StringComparison.Ordinal);
        Assert.Equal((7, "build", "/wt/app", "s1"), ((int)json["before_saved_ms"]!, (string?)json["lost"]![0]!["title"], (string?)json["lost"]![0]!["tab_worktree"], (string?)json["lost"]![0]!["session_id"]));
        Assert.StartsWith("Orca data: /orca\nCompared from: b.db\n\nNo tabs lost.\n", new LostTabsReport("/orca", "/orca/b.db", 7, []).ToText(), StringComparison.Ordinal);
    }

    private static string Fingerprint(params string[] files) =>
        string.Join(' ', files.Select(f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) + File.GetLastWriteTimeUtc(f).Ticks));
}
