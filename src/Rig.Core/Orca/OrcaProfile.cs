using Microsoft.Data.Sqlite;

namespace Rig.Core.Orca;

/// <summary>
/// Orca's saved settings, read from its profile-state database without writing to it: the live
/// <c>profile-state.db</c> first, else the newest <c>profile-state.db.backup.*.db</c> Orca keeps beside it.
/// </summary>
public static class OrcaProfile
{
    /// <summary>The database Orca writes as it runs.</summary>
    public const string LiveDb = "profile-state.db";

    private const string BackupPattern = "profile-state.db.backup.*.db";

    /// <summary>Orca's saved settings and its registered repos, as <c>workspace orca doctor</c> and <c>sync</c> read them.</summary>
    /// <param name="profileDir">The instance's profile folder.</param>
    /// <param name="cli">The <c>orca</c> CLI, for <c>repo list --json</c>.</param>
    /// <param name="launchArgs">What the running Orca launches each agent with, or null when it could not be asked.</param>
    /// <param name="cancellationToken">Cancels the CLI call.</param>
    public static async Task<OrcaSnapshot> SnapshotAsync(string profileDir, IOrcaCli cli, IReadOnlyDictionary<string, string>? launchArgs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var repos = OrcaRepo.ParseList(await cli.RunAsync(["repo", "list", "--json"], cancellationToken).ConfigureAwait(false));
        return new OrcaSnapshot(ReadSettingsCopy(profileDir), repos, profileDir, launchArgs);
    }

    /// <summary>The settings from the live database, else from the newest backup; null when neither can be read.</summary>
    public static SettingsCopy? ReadSettingsCopy(string profileDir)
    {
        var live = Path.Join(profileDir, LiveDb);
        if (ReadSettings(live, live: true) is { } settings)
        {
            // Orca writes through SQLite's write-ahead log, so the log holds the latest save.
            var wal = live + "-wal";
            var saved = new[] { live, wal }.Where(File.Exists).Max(File.GetLastWriteTimeUtc);
            return new SettingsCopy(live, new DateTimeOffset(saved, TimeSpan.Zero), settings);
        }

        if (NewestBackup(profileDir) is not { } backup || ReadSettings(backup, live: false) is not { } backed)
        {
            return null;
        }

        return new SettingsCopy(backup, new DateTimeOffset(File.GetLastWriteTimeUtc(backup), TimeSpan.Zero), backed);
    }

    /// <summary>The most recently written backup database in <paramref name="profileDir"/>, or null.</summary>
    public static string? NewestBackup(string profileDir) =>
        Directory.Exists(profileDir)
            ? Directory.EnumerateFiles(profileDir, BackupPattern).MaxBy(File.GetLastWriteTimeUtc)
            : null;

    /// <summary>
    /// The <c>settings</c> document of a profile-state database, or null when it is absent or unreadable. A
    /// <paramref name="live"/> database, which Orca may be writing, is not opened as immutable, so its log is read.
    /// </summary>
    public static OrcaSettings? ReadSettings(string dbPath, bool live)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        if (!File.Exists(dbPath))
        {
            return null;
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = live ? dbPath : $"file:{ImmutablePath(dbPath)}?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        try
        {
            using var db = new SqliteConnection(builder.ConnectionString);
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "select payload from profile_state_documents where domain = 'settings'";
            return command.ExecuteScalar() is string payload ? OrcaSettings.Parse(payload) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary><paramref name="path"/> escaped as the path part of the SQLite <c>file:</c> name a backup is opened through.</summary>
    public static string ImmutablePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var posix = path.Replace('\\', '/');
        var rooted = posix.StartsWith('/') ? posix : "/" + posix;
        return rooted.Replace("%", "%25", StringComparison.Ordinal).Replace("?", "%3f", StringComparison.Ordinal).Replace("#", "%23", StringComparison.Ordinal);
    }
}
