using Microsoft.Data.Sqlite;
using Rig.Core.Storage;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Storage;

/// <summary><see cref="RigDatabase"/> against databases in a temp folder: migrations, inspection and backups.</summary>
public sealed class RigDatabaseTests : IDisposable
{
    private readonly TempDir temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Db => Path.Join(temp.Path, "nested", "rig.db");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Migrating_an_empty_database_applies_every_version_then_stops()
    {
        Assert.Equal(RigDatabase.CurrentVersion, RigDatabase.Migrate(Db, new ManualClock(new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero))));

        var state = RigDatabase.Inspect(Db);
        Assert.Equal(RigDatabase.CurrentVersion, state.Version);
        Assert.Equal(["001_initial_schema", "002_triage", "003_feature_flags", "004_drop_optical_presets"], state.Applied.Select(m => m.Name));
        Assert.All(state.Applied, m => Assert.EndsWith("2026-11-16T10:00:00.000000+00:00", m.AppliedAt, StringComparison.Ordinal));

        Assert.Equal(0, RigDatabase.Migrate(Db, new ManualClock(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void Inspecting_an_empty_database_reports_zero_and_records_nothing()
    {
        var state = RigDatabase.Inspect(Db);

        Assert.Equal(0, state.Version);
        Assert.Empty(state.Applied);
        using var connection = RigDatabase.Connect(Db);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM schema_migrations";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Opening_returns_a_migrated_connection()
    {
        using var connection = RigDatabase.Open(Db);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal((long)RigDatabase.CurrentVersion, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void A_recorded_migration_with_a_different_name_is_refused()
    {
        using (var connection = RigDatabase.Connect(Db))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL); "
                + "INSERT INTO schema_migrations(version, name, applied_at) VALUES (1, '001_wrong', '2026-01-01');";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<InvalidDataException>(() => RigDatabase.Migrate(Db));

        Assert.Contains("001_wrong", error.Message, StringComparison.Ordinal);
        Assert.Contains("001_initial_schema", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backup_is_a_timestamped_copy_that_keeps_the_schema()
    {
        _ = RigDatabase.Migrate(Db);
        var backupDir = Path.Join(temp.Path, "backups");
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 16, 12, 34, 56, 789, TimeSpan.Zero));

        var path = RigDatabase.Backup(Db, backupDir, clock);

        Assert.Equal(Path.Join(backupDir, "rig_2026-11-16T12-34-56.789Z.db"), path);
        Assert.True(File.Exists(path));
        Assert.Equal(RigDatabase.CurrentVersion, RigDatabase.Inspect(path).Version);
    }

    [Fact]
    public void Backing_up_a_missing_database_names_the_path()
    {
        var error = Assert.Throws<FileNotFoundException>(() => RigDatabase.Backup(Db, temp.Path, TimeProvider.System));

        Assert.Equal(Db, error.FileName);
        Assert.Contains("Database file does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backups_beyond_the_limit_are_pruned_oldest_first()
    {
        _ = RigDatabase.Migrate(Db);
        var backupDir = temp.Folder("backups");
        foreach (var (name, age) in new[] { ("rig_old.db", 3), ("rig_middle.db", 2), ("rig_new.db", 1) })
        {
            var path = Path.Join(backupDir, name);
            await File.WriteAllTextAsync(path, "old", Token);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age));
        }

        var created = RigDatabase.Backup(Db, backupDir, TimeProvider.System, maxBackups: 2);

        var left = Directory.GetFiles(backupDir, "rig_*.db").Select(Path.GetFileName).ToArray();
        Assert.Equal(2, left.Length);
        Assert.Contains(Path.GetFileName(created), left);
        Assert.DoesNotContain("rig_old.db", left);
    }

    [Fact]
    public void Empty_paths_are_refused()
    {
        Assert.Throws<ArgumentException>(() => RigDatabase.Connect(string.Empty));
        Assert.Throws<ArgumentException>(() => RigDatabase.Migrate(string.Empty));
        Assert.Throws<ArgumentException>(() => RigDatabase.Inspect(string.Empty));
        Assert.Throws<ArgumentException>(() => RigDatabase.Backup(Db, string.Empty, TimeProvider.System));
        Assert.Throws<ArgumentOutOfRangeException>(() => RigDatabase.Backup(Db, temp.Path, TimeProvider.System, maxBackups: 0));
        Assert.Throws<ArgumentException>(() => RigDatabase.Backup(string.Empty, temp.Path, TimeProvider.System));
    }

    [Fact]
    public void A_migrated_database_records_the_recorded_columns()
    {
        using var connection = RigDatabase.Open(Db);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("feature_flags", tables);
        Assert.Contains("job_runs", tables);
        Assert.Contains("schema_migrations", tables);
        Assert.Contains("triage_events", tables);
        Assert.Contains("triage_items", tables);
        Assert.DoesNotContain("optical_presets", tables);
    }

    [Fact]
    public void A_backup_directory_that_does_not_exist_is_created()
    {
        _ = RigDatabase.Migrate(Db);
        var directory = Path.Join(temp.Path, "deep", "backups");

        var path = RigDatabase.Backup(Db, directory, TimeProvider.System, maxBackups: 10);

        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Sqlite_errors_from_a_directory_database_are_not_hidden()
    {
        var directory = temp.Folder("not-a-database");

        Assert.Throws<SqliteException>(() => RigDatabase.Migrate(directory));
    }
}
