namespace Rig.Core.Processes;

/// <summary>Finds a program on a PATH value the way a shell does, like <c>which(1)</c>.</summary>
public static class ProgramPath
{
    /// <summary>
    /// The first runnable file named <paramref name="program"/> in the folders of <paramref name="path"/>, or null. On Windows
    /// each of the <paramref name="extensions"/> (the <c>PATHEXT</c> list, separated by <c>;</c>) is tried, and the bare name
    /// only when it already ends in one of them.
    /// </summary>
    public static string? Find(string program, string? path, string? extensions = null) =>
        FindAll(program, path, extensions).FirstOrDefault();

    /// <summary>
    /// Every runnable file named <paramref name="program"/> in the folders of <paramref name="path"/>, in the order a shell
    /// would try them, like <c>which -a</c>. The first is what <see cref="Find(string, string?, string?)"/> returns; the
    /// rest are shadowed by it. Extensions work as they do there.
    /// </summary>
    public static IEnumerable<string> FindAll(string program, string? path, string? extensions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        // With a PATHEXT list, the bare name only counts when it already ends in one
        // of the extensions, so an extensionless shim beside npm.cmd is never picked.
        var listed = (extensions ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bare = listed.Length == 0 || listed.Any(extension => program.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        string[] suffixes = bare ? [string.Empty, .. listed] : listed;
        var folders = (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return folders
            .SelectMany(folder => suffixes.Select(suffix => Path.Combine(folder, program + suffix)))
            .Where(Runnable);
    }

    // Like shutil.which: on Unix a file without an execute bit is skipped, so a stray data file named like the program
    // never shadows the real one further down PATH. Windows runs by extension, which PATHEXT already covers.
    private static bool Runnable(string candidate) =>
        File.Exists(candidate)
        && (OperatingSystem.IsWindows()
            || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);

    /// <summary>Finds <paramref name="program"/> on this process's <c>PATH</c>.</summary>
    public static string? Find(string program) =>
        Find(
            program,
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATHEXT") : null);
}
