using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Rig.Core.Flags;

/// <summary>
/// The flag overrides stored in the <c>feature_flags</c> table of <c>rig.db</c>. Each call opens the database, makes
/// sure the table exists (the same definition as Python's migration <c>003_feature_flags</c>, so the two agree on one
/// file) and closes it again.
/// </summary>
/// <param name="dbPath">The database file; its folder is created when missing.</param>
/// <param name="clock">The clock that stamps <c>updated_at</c>; the system clock when null.</param>
public sealed class FlagStore(string dbPath, TimeProvider? clock = null)
{
    /// <summary>The table's definition, as Python's migration creates it.</summary>
    public const string Schema =
        "CREATE TABLE IF NOT EXISTS feature_flags ( name TEXT PRIMARY KEY, enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)), updated_at TEXT NOT NULL )";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Every declared flag with its stored override, if any; rows for undeclared names are ignored.</summary>
    public IReadOnlyList<FlagState> States()
    {
        var overrides = new Dictionary<string, bool>(StringComparer.Ordinal);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, enabled FROM feature_flags";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                overrides[reader.GetString(0)] = reader.GetInt64(1) != 0;
            }
        }

        return [.. FeatureFlags.All.Select(flag => new FlagState(flag, overrides.TryGetValue(flag.Name, out var on) ? on : null))];
    }

    /// <summary>Each declared flag's on/off state: the stored override, else the default.</summary>
    public IReadOnlyDictionary<string, bool> Effective() =>
        States().ToDictionary(state => state.Flag.Name, state => state.Enabled, StringComparer.Ordinal);

    /// <summary>Whether the flag called <paramref name="name"/> is on.</summary>
    /// <exception cref="UnknownFlagException">No flag has that name.</exception>
    public bool IsEnabled(string name)
    {
        FeatureFlags.Named(name);
        return Effective()[name];
    }

    /// <summary>Store an override turning <paramref name="name"/> on or off.</summary>
    /// <exception cref="UnknownFlagException">No flag has that name.</exception>
    public void Set(string name, bool enabled)
    {
        FeatureFlags.Named(name);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO feature_flags (name, enabled, updated_at) VALUES ($name, $enabled, $updated) "
            + "ON CONFLICT(name) DO UPDATE SET enabled = excluded.enabled, updated_at = excluded.updated_at";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$updated",
            _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Remove the stored override for <paramref name="name"/>; true when there was one.</summary>
    /// <exception cref="UnknownFlagException">No flag has that name.</exception>
    public bool Clear(string name)
    {
        FeatureFlags.Named(name);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM feature_flags WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteNonQuery() > 0;
    }

    private SqliteConnection Open()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(dbPath)) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; "
            + Schema;
        command.ExecuteNonQuery();
        return connection;
    }
}
