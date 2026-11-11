using Microsoft.Data.Sqlite;
using Rig.Core.Orca;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Orca;

/// <summary>Where an Orca instance keeps its files, its saved settings, and its CLI. Ported from <c>tests/test_workspace_orca.py</c>.</summary>
public sealed class OrcaProfileTests : IDisposable
{
    private const string Settings = """{"agentDefaultArgs": {"claude": ""}, "workspaceDir": "~/orca/workspaces"}""";

    private static readonly DateTime Older = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Newer = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    private readonly TempDir _dir = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    public static TheoryData<OrcaOs, string?, string?, string> DefaultCases => new()
    {
        { OrcaOs.MacOs, null, null, "/h/Library/Application Support/orca" },
        { OrcaOs.Windows, null, null, "/h/AppData/Roaming/orca" },
        { OrcaOs.Windows, "/roaming", null, "/roaming/orca" },
        { OrcaOs.Linux, null, null, "/h/.config/orca" },
        { OrcaOs.Linux, null, "/xdg", "/xdg/orca" },
    };

    [Theory]
    [MemberData(nameof(DefaultCases))]
    public void The_default_instance_lives_where_the_platform_keeps_app_data(OrcaOs os, string? appData, string? xdg, string expected)
    {
        var locations = Locations(os, ("APPDATA", appData), ("XDG_CONFIG_HOME", xdg));

        Assert.Equal(expected, Slashed(locations.DefaultUserData()));
    }

    [Fact]
    public void A_user_data_folder_holds_everything()
    {
        var locations = Locations(OrcaOs.Linux, ("ORCA_RUNTIME_JSON", "/elsewhere.json"), ("ORCA_PROFILE_DIR", "/elsewhere"));

        Assert.Equal("/d/orca-runtime.json", Slashed(locations.RuntimeMetadata("/d")));
        Assert.Equal("/d/profiles/local-default", Slashed(locations.ProfileDir("/d")));
    }

    [Fact]
    public void The_default_instance_honours_its_overrides()
    {
        var overridden = Locations(OrcaOs.Linux, ("ORCA_RUNTIME_JSON", "~/rt.json"), ("ORCA_PROFILE_DIR", "~"));
        var plain = Locations(OrcaOs.Linux);

        Assert.Equal(("/h/rt.json", "/h"), (Slashed(overridden.RuntimeMetadata(null)), Slashed(overridden.ProfileDir(null))));
        Assert.Equal("/h/.config/orca/orca-runtime.json", Slashed(plain.RuntimeMetadata(null)));
        Assert.Equal("/h/.config/orca/profiles/local-default", Slashed(plain.ProfileDir(null)));
        Assert.Equal("/abs", Locations(OrcaOs.Linux, ("ORCA_PROFILE_DIR", "/abs")).ProfileDir(null));
    }

    [Fact]
    public void The_flag_beats_the_variable_which_beats_the_default()
    {
        var withVariable = Locations(OrcaOs.Linux, (OrcaLocations.UserDataVariable, "/env"));

        Assert.Equal("/flag", withVariable.UserData("/flag"));
        Assert.Equal("/env", withVariable.UserData(null));
        Assert.Null(Locations(OrcaOs.Linux, (OrcaLocations.UserDataVariable, "")).UserData(""));
    }

    [Fact]
    public void Settings_come_from_the_live_database_with_its_log_time()
    {
        var live = Database(OrcaProfile.LiveDb, Settings, Older);
        File.WriteAllText(live + "-wal", "");
        File.SetLastWriteTimeUtc(live + "-wal", Newer);
        Database("profile-state.db.backup.1.db", """{"workspaceDir": "/backup"}""", Newer);

        var copy = OrcaProfile.ReadSettingsCopy(_dir.Path);

        Assert.NotNull(copy);
        Assert.Equal((live, Newer), (copy.Source, copy.Saved.UtcDateTime));
        Assert.Equal("~/orca/workspaces", copy.Settings.WorkspaceDir);
    }

    [Fact]
    public void Without_a_live_database_the_newest_backup_is_read()
    {
        Database("profile-state.db.backup.1.db", """{"workspaceDir": "/old"}""", Older);
        var newest = Database("profile-state.db.backup.2.db", """{"workspaceDir": "/new"}""", Newer);
        _dir.Folder("profile-state.db.backup.3.db");

        var copy = OrcaProfile.ReadSettingsCopy(_dir.Path);

        Assert.NotNull(copy);
        Assert.Equal((newest, Newer, "/new"), (copy.Source, copy.Saved.UtcDateTime, copy.Settings.WorkspaceDir));
    }

    [Fact]
    public void Unreadable_stores_read_as_nothing()
    {
        File.WriteAllText(Path.Join(_dir.Path, OrcaProfile.LiveDb), "not a database");
        Database("profile-state.db.backup.1.db", "not json", Older);

        Assert.Null(OrcaProfile.ReadSettingsCopy(_dir.Path));
        Assert.Null(OrcaProfile.ReadSettingsCopy(Path.Join(_dir.Path, "absent")));
        Assert.Null(OrcaProfile.NewestBackup(Path.Join(_dir.Path, "absent")));
    }

    [Fact]
    public void A_database_without_settings_reads_as_nothing()
    {
        var empty = Database("empty.db", null, Older);

        Assert.Null(OrcaProfile.ReadSettings(empty, live: false));
        Assert.Null(OrcaProfile.ReadSettings(Path.Join(_dir.Path, "absent.db"), live: true));
    }

    [Fact]
    public void A_backup_in_a_folder_with_awkward_characters_still_opens()
    {
        var folder = _dir.Folder("Application Support #1 %20 ?x");
        var path = Path.Join(folder, "b.db");
        File.Copy(Database("seed.db", Settings, Older), path);

        Assert.Equal("~/orca/workspaces", OrcaProfile.ReadSettings(path, live: false)?.WorkspaceDir);
        Assert.Equal("/C:/a%25b%3f%23", OrcaProfile.ImmutablePath(@"C:\a%b?#"));
    }

    [Fact]
    public async Task A_snapshot_reads_settings_repos_and_launch_lines()
    {
        Database(OrcaProfile.LiveDb, Settings, Older);
        var cli = new FakeOrcaCli(_ => """{"result": {"repos": [{"path": "/code/rig"}]}}""");
        var launch = new Dictionary<string, string> { ["claude"] = "" };

        var snapshot = await OrcaProfile.SnapshotAsync(_dir.Path, cli, launch, Token);

        Assert.Equal(["repo", "list", "--json"], Assert.Single(cli.Calls));
        Assert.Equal("/code/rig", Assert.Single(snapshot.Repos!).Path);
        Assert.Equal((_dir.Path, launch), (snapshot.ProfileDir, snapshot.LaunchArgs));
        Assert.NotNull(snapshot.Settings);
    }

    [Fact]
    public async Task The_cli_targets_an_instance_through_its_variable()
    {
        var proc = new FakeProc().Answer(new Ran(0, "out", ""), "orca", "repo");
        var targeted = new OrcaCliRunner(proc, "/data");
        var plain = new OrcaCliRunner(proc, null);

        Assert.Equal("out", await targeted.RunAsync(["repo", "list"], Token));
        Assert.Null(await plain.RunAsync(["status"], Token));
        Assert.Equal(["orca", "repo", "list"], proc.Requests[0].Argv);
        Assert.Equal("/data", proc.Requests[0].Environment[OrcaLocations.UserDataVariable]);
        Assert.Empty(proc.Requests[1].Environment);
    }

    private static OrcaLocations Locations(OrcaOs os, params (string Name, string? Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return new OrcaLocations(name => map.GetValueOrDefault(name), "/h", os);
    }

    private static string Slashed(string path) => path.Replace('\\', '/');

    /// <summary>A profile-state database holding <paramref name="payload"/> as its settings document (none when null).</summary>
    private string Database(string name, string? payload, DateTime written)
    {
        var path = Path.Join(_dir.Path, name);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
        {
            db.Open();
            using var create = db.CreateCommand();
            create.CommandText = "create table profile_state_documents (domain text, payload text)";
            create.ExecuteNonQuery();
            if (payload is not null)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = "insert into profile_state_documents values ('settings', $payload)";
                insert.Parameters.AddWithValue("$payload", payload);
                insert.ExecuteNonQuery();
            }
        }

        File.SetLastWriteTimeUtc(path, written);
        return path;
    }
}
