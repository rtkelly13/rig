namespace Rig.Core.Roots;

/// <summary>What <see cref="AppDataMigration.CopyLegacy"/> did.</summary>
/// <param name="Legacy">The old folder, or null when there is none to copy from.</param>
/// <param name="Copied">Top-level entries copied into the new folder.</param>
/// <param name="Kept">Top-level entries left alone because the new folder already has them.</param>
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
public static class AppDataMigration
{
    /// <summary>
    /// The entry never copied: runtime copies hold absolute paths into their own folder (the <c>current</c> junction, each
    /// copy's environment), so the new folder gets a fresh install instead.
    /// </summary>
    public const string RuntimeEntry = "runtime";

    /// <summary>
    /// Copies each top-level entry of the legacy folder beside <paramref name="appData"/> that <paramref name="appData"/>
    /// does not have yet, except <see cref="RuntimeEntry"/>. Safe to repeat: an entry already present is kept as it is.
    /// </summary>
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
        foreach (var source in Directory.EnumerateFileSystemEntries(legacy).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(source);
            if (name == RuntimeEntry || IsLink(source))
            {
                continue;
            }

            var target = Path.Join(full, name);
            if (Path.Exists(target))
            {
                kept.Add(name);
                continue;
            }

            if (Directory.Exists(source))
            {
                CopyTree(source, target, failed);
            }
            else
            {
                CopyFile(source, target, failed);
            }

            copied.Add(name);
        }

        return new(legacy, copied, kept, failed);
    }

    private static void CopyTree(string source, string destination, List<string> failed)
    {
        _ = Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if (IsLink(entry))
            {
                continue;
            }

            var target = Path.Join(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyTree(entry, target, failed);
            }
            else
            {
                CopyFile(entry, target, failed);
            }
        }
    }

    private static void CopyFile(string source, string destination, List<string> failed)
    {
        try
        {
            File.Copy(source, destination, overwrite: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            failed.Add($"{source}: {error.Message}");
        }
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;
}
