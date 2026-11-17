using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Rig.Core.Storage;

/// <summary>The shared rig SQLite database and the numbered migrations.</summary>
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
            CREATE TABLE IF NOT EXISTS triage_items (
                repo TEXT NOT NULL, number INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (repo, number)
            );
            CREATE TABLE IF NOT EXISTS triage_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, repo TEXT NOT NULL, number INTEGER NOT NULL,
                at TEXT NOT NULL, action TEXT NOT NULL, head TEXT NOT NULL, note TEXT NOT NULL,
                actor TEXT NOT NULL DEFAULT 'local', source_key TEXT UNIQUE,
                FOREIGN KEY (repo, number) REFERENCES triage_items(repo, number)
            );
            CREATE INDEX IF NOT EXISTS triage_event_item ON triage_events(repo, number, id);
            """),
        new(3, "003_feature_flags", """
            CREATE TABLE IF NOT EXISTS feature_flags (
                name TEXT PRIMARY KEY, enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)), updated_at TEXT NOT NULL
            );
            """),
        new(4, "004_drop_optical_presets", "DROP TABLE IF EXISTS optical_presets;"),
    ];

    /// <summary>The highest migration this code knows; the schema version a fully migrated database holds.</summary>
    public static int CurrentVersion => Migrations[^1].Version;

    /// <summary>Open a WAL database, apply pending migrations, and return ownership to the caller.</summary>
    public static SqliteConnection Open(string path, TimeProvider? clock = null)
    {
        var connection = Connect(path);
        try
        {
            _ = Migrate(connection, clock ?? TimeProvider.System);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Open a WAL database without applying migrations; ownership belongs to the caller.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public static SqliteConnection Connect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString());
        try
        {
            connection.Open();
            using var setup = connection.CreateCommand();
            setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
            setup.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Apply every pending migration to <paramref name="path"/> and return how many ran.</summary>
    /// <exception cref="InvalidDataException">A recorded migration name disagrees with this code's numbering.</exception>
    public static int Migrate(string path, TimeProvider? clock = null)
    {
        using var connection = Connect(path);
        return Migrate(connection, clock ?? TimeProvider.System);
    }

    /// <summary>Read the schema version and applied migrations without changing either.</summary>
    /// <remarks>The <c>schema_migrations</c> table is created when absent, so an empty database answers 0.</remarks>
    public static SchemaState Inspect(string path)
    {
        using var connection = Connect(path);
        EnsureHistoryTable(connection);
        return ReadState(connection);
    }

    /// <summary>
    /// Take a consistent online copy of <paramref name="path"/> into <paramref name="backupDirectory"/> and prune the
    /// oldest copies beyond <paramref name="maxBackups"/>. Returns the created file's path.
    /// </summary>
    /// <exception cref="FileNotFoundException">The database does not exist yet.</exception>
    public static string Backup(string path, string backupDirectory, TimeProvider clock, int maxBackups = 10)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(backupDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBackups);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Database file does not exist: {path}", path);
        }

        _ = Directory.CreateDirectory(backupDirectory);
        var stamp = clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH-mm-ss.fff'Z'", CultureInfo.InvariantCulture);
        var destination = Path.Join(backupDirectory, $"rig_{stamp}.db");
        using (var source = Connect(path))
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString()))
        {
            target.Open();
            source.BackupDatabase(target);
        }

        Prune(backupDirectory, maxBackups);
        return destination;
    }

    private static int Migrate(SqliteConnection connection, TimeProvider clock)
    {
        // Hold the writer lock across the history read and every migration, including on a concurrently created DB.
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

        var applied = 0;
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
            applied++;
        }

        transaction.Commit();
        return applied;
    }

    private static void EnsureHistoryTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }

    private static SchemaState ReadState(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name, applied_at FROM schema_migrations ORDER BY version";
        var applied = new List<AppliedMigration>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            applied.Add(new AppliedMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return new SchemaState(applied.Count == 0 ? 0 : applied[^1].Version, applied);
    }

    private static void Prune(string backupDirectory, int maxBackups)
    {
        var backups = Directory.GetFiles(backupDirectory, "rig_*.db")
            .OrderBy(file => File.GetLastWriteTimeUtc(file))
            .ThenBy(file => file, StringComparer.Ordinal)
            .ToArray();
        foreach (var old in backups.Take(Math.Max(0, backups.Length - maxBackups)))
        {
            File.Delete(old);
        }
    }
}

/// <summary>One migration a database records as applied.</summary>
/// <param name="Version">The migration's number.</param>
/// <param name="Name">Its stable name, <c>NNN_slug</c>.</param>
/// <param name="AppliedAt">When it was applied, as the database recorded it.</param>
public sealed record AppliedMigration(int Version, string Name, string AppliedAt);

/// <summary>What a database currently holds: its schema version and its applied migrations.</summary>
/// <param name="Version">The highest applied version, or 0 when nothing has been applied.</param>
/// <param name="Applied">Every applied migration, oldest first.</param>
public sealed record SchemaState(int Version, IReadOnlyList<AppliedMigration> Applied);
