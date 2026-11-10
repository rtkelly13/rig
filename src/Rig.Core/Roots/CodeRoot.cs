namespace Rig.Core.Roots;

/// <summary>
/// Where the code root is: the folder every repo is cloned into (<c>~/code</c> on this estate). It is marked by an
/// <c>AGENTS.md</c> beside a <c>personal/</c> folder. Resolution order: <c>CODE_ROOT</c>; the nearest ancestor of
/// the start folder carrying the marker (which still finds the root from a worktree nested in a repo); then
/// <c>~/code</c>, else <c>&lt;drive&gt;/code</c>.
/// </summary>
public static class CodeRoot
{
    /// <summary>The environment variable that overrides the code root.</summary>
    public const string EnvironmentVariable = "CODE_ROOT";

    /// <summary>The personal folder's name, half of the marker that finds the root.</summary>
    public const string PersonalFolder = "personal";

    private const string MarkerFile = "AGENTS.md";

    /// <summary>Whether <paramref name="path"/> carries the marker: an <c>AGENTS.md</c> beside <c>personal/</c>.</summary>
    public static bool IsCodeRoot(string path) =>
        File.Exists(Path.Combine(path, MarkerFile)) && Directory.Exists(Path.Combine(path, PersonalFolder));

    /// <summary>The nearest of <paramref name="start"/> and its ancestors that is a code root, or null.</summary>
    public static string? Find(string start)
    {
        for (var candidate = new DirectoryInfo(Path.GetFullPath(start)); candidate is not null; candidate = candidate.Parent)
        {
            if (IsCodeRoot(candidate.FullName))
            {
                return candidate.FullName;
            }
        }

        return null;
    }

    /// <summary>The fallback: <c>&lt;home&gt;/code</c>, else <c>&lt;drive&gt;/code</c>, else <c>&lt;home&gt;/code</c> anyway.</summary>
    public static string HomeFallback(string home)
    {
        var atHome = Path.Combine(home, "code");
        var onDrive = Path.Combine(Path.GetPathRoot(Path.GetFullPath(home)) ?? home, "code");
        return Directory.Exists(atHome) ? atHome : Directory.Exists(onDrive) ? onDrive : atHome;
    }

    /// <summary>
    /// The code root, given the <c>CODE_ROOT</c> value (if any), the folder to search up from and the home folder.
    /// </summary>
    public static string Resolve(string? configured, string start, string home)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (!string.IsNullOrEmpty(configured))
        {
            return ExpandHome(configured, home);
        }

        return Find(start) ?? HomeFallback(home);
    }

    /// <summary>The code root for this process: its environment, the app's base folder and the user's home.</summary>
    public static string Current() =>
        Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static string ExpandHome(string path, string home) =>
        path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..])
            : path;
}
