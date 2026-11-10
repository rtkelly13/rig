namespace Rig.Core.Processes;

/// <summary>Finds a program on a PATH value the way a shell does, like <c>which(1)</c> and Python's <c>shutil.which</c>.</summary>
public static class ProgramPath
{
    /// <summary>
    /// The first runnable file named <paramref name="program"/> in the folders of <paramref name="path"/>, or null. On Windows
    /// the <paramref name="extensions"/> (the <c>PATHEXT</c> list, separated by <c>;</c>) are tried as well.
    /// </summary>
    public static string? Find(string program, string? path, string? extensions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        var suffixes = new List<string> { string.Empty };
        suffixes.AddRange((extensions ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var folders = (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return folders
            .SelectMany(folder => suffixes.Select(suffix => Path.Combine(folder, program + suffix)))
            .FirstOrDefault(Runnable);
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
