using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Rig.Core.Storage;

/// <summary>The shared rig SQLite database and the numbered migrations already used by Python.</summary>
public static class RigDatabase
{
    private sealed record Migration(int Version, string Name, string Sql);

    private static readonly Migration[] Migrations =
    [
        new(1, "001_initial_schema", """
            CREATE TABLE IF NOT EXISTS job_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_name TEXT NOT NULL, trigger TEXT NOT NULL, environment TEXT NOT NULL,
                started_at TEXT NOT NULL, finished_at TEXT, status TEXT NOT NULL, exit_code INTEGER, output TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_job_runs_started_at ON job_runs(started_at DESC);
            CREATE TABLE IF NOT EXISTS optical_presets (
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, project TEXT NOT NULL,
                ior REAL NOT NULL, dispersion REAL NOT NULL, roughness REAL NOT NULL, thickness REAL NOT NULL,
                distortion REAL NOT NULL, caustic_color TEXT NOT NULL, created_at TEXT NOT NULL
            );
            """),
        new(2, "002_triage", """
            CREATE TABLE triage_items (
                repo TEXT NOT NULL, number INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (repo, number)
            );
            CREATE TABLE triage_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, repo TEXT NOT NULL, number INTEGER NOT NULL,
                at TEXT NOT NULL, action TEXT NOT NULL, head TEXT NOT NULL, note TEXT NOT NULL,
                actor TEXT NOT NULL DEFAULT 'local', source_key TEXT UNIQUE,
                FOREIGN KEY (repo, number) REFERENCES triage_items(repo, number)
            );
            CREATE INDEX triage_event_item ON triage_events(repo, number, id);
            """),
        new(3, "003_feature_flags", """
            CREATE TABLE IF NOT EXISTS feature_flags (
                name TEXT PRIMARY KEY, enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)), updated_at TEXT NOT NULL
            );
            """),
        new(4, "004_drop_optical_presets", "DROP TABLE IF EXISTS optical_presets;"),
    ];

    /// <summary>Open a WAL database, apply pending migrations, and return ownership to the caller.</summary>
    public static SqliteConnection Open(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString());
            connection.Open();
            using var setup = connection.CreateCommand();
            setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
            setup.ExecuteNonQuery();
            Migrate(connection, clock ?? TimeProvider.System);
            var result = connection;
            connection = null;
            return result;
        }
        finally
        {
            connection?.Dispose();
        }
    }

    // Hold the writer lock across the history read and every migration, including on a concurrently created DB.
    private static void Migrate(SqliteConnection connection, TimeProvider clock)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT version, name FROM schema_migrations ORDER BY version";
        var current = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var version = reader.GetInt32(0);
                var known = Migrations.FirstOrDefault(m => m.Version == version);
                if (known is not null && reader.GetString(1) != known.Name)
                {
                    throw new InvalidDataException($"Database recorded version {version} as {reader.GetString(1)}, but this code numbers {known.Name} as version {version}");
                }

                current = Math.Max(current, version);
            }
        }

        foreach (var migration in Migrations.Where(m => m.Version > current))
        {
            // SQL comes only from the private, fixed migration list above; no external input enters it.
#pragma warning disable CA2100
            command.CommandText = migration.Sql;
#pragma warning restore CA2100
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO schema_migrations(version, name, applied_at) VALUES ($version, $name, $at)";
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$name", migration.Name);
            command.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
#pragma warning disable CA2100 // Only a version integer from the fixed migration list is interpolated.
            command.CommandText = $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)}";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
