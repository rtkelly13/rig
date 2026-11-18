using Microsoft.Data.Sqlite;
using Rig.Core.Roots;

namespace Rig.Core.Tests.Roots;

/// <summary><see cref="AppDataMigration"/> copying state out of the folder's pre-rename name.</summary>
public sealed class AppDataMigrationTests : IDisposable
{
    private readonly TempDir _temp = new();

    private string Legacy => Path.Join(_temp.Path, AppData.LegacyName);

    private string Current => Path.Join(_temp.Path, AppData.Name);

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void State_is_copied_and_the_legacy_folder_is_left_in_place()
    {
        Write(Path.Join(Legacy, "rig.db"), "db");
        Write(Path.Join(Legacy, "jobs", "state.json"), "{}");
        Write(Path.Join(Legacy, "jobs", "logs", "restore.log"), "log");

        var result = AppDataMigration.CopyLegacy(Current);

        Assert.Equal(Legacy, result.Legacy);
        Assert.Equal(["jobs", "rig.db"], result.Copied);
        Assert.Equal("db", File.ReadAllText(Path.Join(Current, "rig.db")));
        Assert.Equal("log", File.ReadAllText(Path.Join(Current, "jobs", "logs", "restore.log")));
        Assert.True(File.Exists(Path.Join(Legacy, "rig.db")));
    }

    [Fact]
    public void Runtime_copies_are_never_copied()
    {
        Write(Path.Join(Legacy, "runtime", "state.json"), "{}");
        Write(Path.Join(Legacy, "traces", "a.ndjson"), "{}");

        var result = AppDataMigration.CopyLegacy(Current);

        Assert.Equal(["traces"], result.Copied);
        Assert.False(Directory.Exists(Path.Join(Current, "runtime")));
    }

    [Fact]
    public void An_entry_the_new_folder_already_has_is_kept_and_a_second_run_copies_nothing()
    {
        Write(Path.Join(Legacy, "rig.db"), "old");
        Write(Path.Join(Legacy, "otel", "x"), "x");
        Write(Path.Join(Current, "rig.db"), "new");

        var first = AppDataMigration.CopyLegacy(Current);
        var second = AppDataMigration.CopyLegacy(Current);

        Assert.Equal(["otel"], first.Copied);
        Assert.Equal(["rig.db"], first.Kept);
        Assert.Equal("new", File.ReadAllText(Path.Join(Current, "rig.db")));
        Assert.Empty(second.Copied);
        Assert.Equal(["otel", "rig.db"], second.Kept);
    }

    [Fact]
    public void A_folder_copied_in_part_gets_its_missing_files_on_the_next_run()
    {
        Write(Path.Join(Legacy, "jobs", "state.json"), "{}");
        Write(Path.Join(Legacy, "jobs", "logs", "restore.log"), "log");
        Write(Path.Join(Current, "jobs", "state.json"), "newer");

        var result = AppDataMigration.CopyLegacy(Current);

        Assert.Equal(["jobs"], result.Copied);
        Assert.Equal("newer", File.ReadAllText(Path.Join(Current, "jobs", "state.json")));
        Assert.Equal("log", File.ReadAllText(Path.Join(Current, "jobs", "logs", "restore.log")));
    }

    [Fact]
    public void A_database_open_in_wal_mode_is_copied_with_its_committed_rows_and_without_its_sidecars()
    {
        _ = Directory.CreateDirectory(Legacy);
        var source = Path.Join(Legacy, "rig.db");
        using (var writer = Open(source))
        {
            using (var setup = writer.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('a'), ('b');";
                _ = setup.ExecuteNonQuery();
            }

            Assert.True(File.Exists(source + "-wal"));

            var result = AppDataMigration.CopyLegacy(Current);

            Assert.Equal(["rig.db"], result.Copied);
            Assert.Empty(result.Failed);
        }

        Assert.False(File.Exists(Path.Join(Current, "rig.db-wal")));
        using var copy = Open(Path.Join(Current, "rig.db"));
        using var count = copy.CreateCommand();
        count.CommandText = "SELECT count(*) FROM t";
        Assert.Equal(2L, count.ExecuteScalar());
    }

    [Fact]
    public void Nothing_happens_without_a_legacy_folder()
    {
        var result = AppDataMigration.CopyLegacy(Current);

        Assert.Null(result.Legacy);
        Assert.False(Directory.Exists(Current));
    }

    [Fact]
    public void The_legacy_folder_never_copies_into_itself()
    {
        Write(Path.Join(Legacy, "rig.db"), "db");

        Assert.Null(AppDataMigration.CopyLegacy(Legacy).Legacy);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Write(string path, string text)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
