using Microsoft.Data.Sqlite;

namespace Rig.Core.Roots;

/// <summary>What <see cref="AppDataMigration.CopyLegacy"/> did.</summary>
/// <param name="Legacy">The old folder, or null when there is none to copy from.</param>
/// <param name="Copied">Top-level entries that received at least one file from the old folder.</param>
/// <param name="Kept">Top-level entries the new folder already had in full, left alone.</param>
/// <param name="Failed">Files that could not be copied (locked or unreadable), with the reason.</param>
public sealed record AppDataMigrationResult(
    string? Legacy,
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Failed);

/// <summary>
/// Moves rig's state from the folder it had before the repo was renamed (<see cref="AppData.LegacyName"/>) to
/// <see cref="AppData.Name"/>. It copies rather than moves: the scheduled tasks keep running the old runtime copy, which
/// still writes to the old folder, until <c>rig jobs install</c> re-registers them, and the old folder is the rollback.
/// </summary>
// TODO(2027-01-31): delete with the rest of the shared-utilities -> rig migration (AppDataMigration, the legacy task names) once every machine has run `rig runtime install` and `rig jobs install`.
public static class AppDataMigration
{
    /// <summary>
    /// The entry never copied: runtime copies hold absolute paths into their own folder (the <c>current</c> junction, each
    /// copy's environment), so the new folder gets a fresh install instead.
    /// </summary>
    public const string RuntimeEntry = "runtime";

    private static readonly string[] SqliteSidecars = ["-wal", "-shm", "-journal"];

    private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;

    /// <summary>
    /// Copies each top-level entry of the legacy folder beside <paramref name="appData"/> into <paramref name="appData"/>,
    /// except <see cref="RuntimeEntry"/>. A file the new folder already has is kept as it is; a folder it already has gets
    /// only the files it is missing, so a run that failed on a locked file completes on the next one.
    /// </summary>
    /// <remarks>
    /// A SQLite database is copied through SQLite's online backup rather than byte for byte. The old runtime may hold it
    /// open in WAL mode, and copying the database and its <c>-wal</c> file separately could tear a transaction; the backup
    /// reads one consistent snapshot, committed WAL pages included, so its sidecar files are never copied.
    /// </remarks>
    /// <param name="appData">The current app-data folder (<see cref="AppData.Current"/>).</param>
    public static AppDataMigrationResult CopyLegacy(string appData)
    {
        ArgumentException.ThrowIfNullOrEmpty(appData);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appData));
        if (Path.GetDirectoryName(full) is not { } parent || Path.GetFileName(full) == AppData.LegacyName)
        {
            return new(null, [], [], []);
        }

        var legacy = Path.Join(parent, AppData.LegacyName);
        if (!Directory.Exists(legacy))
        {
            return new(null, [], [], []);
        }

        _ = Directory.CreateDirectory(full);
        var copied = new List<string>();
        var kept = new List<string>();
        var failed = new List<string>();
        foreach (var source in Entries(legacy))
        {
            var name = Path.GetFileName(source);
            if (name == RuntimeEntry)
            {
                continue;
            }

            var target = Path.Join(full, name);
            var existed = Path.Exists(target);
            var any = Directory.Exists(source) ? CopyTree(source, target, failed) : CopyFile(source, target, failed);
            (any || !existed ? copied : kept).Add(name);
        }

        return new(legacy, copied, kept, failed);
    }

    /// <summary>The folder's entries, without links and without the sidecar files of a SQLite database beside them.</summary>
    private static IEnumerable<string> Entries(string folder)
    {
        var all = Directory.EnumerateFileSystemEntries(folder).Where(entry => !IsLink(entry)).Order(StringComparer.Ordinal).ToList();
        return all.Where(entry => !IsSidecar(entry));

        static bool IsSidecar(string entry) =>
            SqliteSidecars.FirstOrDefault(suffix => entry.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) is { } suffix
            && IsSqlite(entry[..^suffix.Length]);
    }

    private static bool CopyTree(string source, string destination, List<string> failed)
    {
        _ = Directory.CreateDirectory(destination);
        var any = false;
        foreach (var entry in Entries(source))
        {
            var target = Path.Join(destination, Path.GetFileName(entry));
            any |= Directory.Exists(entry) ? CopyTree(entry, target, failed) : CopyFile(entry, target, failed);
        }

        return any;
    }

    private static bool CopyFile(string source, string destination, List<string> failed)
    {
        if (Path.Exists(destination))
        {
            return false;
        }

        try
        {
            if (IsSqlite(source))
            {
                BackupSqlite(source, destination);
            }
            else
            {
                File.Copy(source, destination, overwrite: false);
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            failed.Add($"{source}: {error.Message}");
            return false;
        }
    }

    private static void BackupSqlite(string source, string destination)
    {
        var partial = destination + ".partial";
        File.Delete(partial);
        using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = partial, Pooling = false }.ToString()))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
        }

        File.Move(partial, destination);
    }

    private static bool IsSqlite(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header.SequenceEqual(SqliteHeader);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;
}
