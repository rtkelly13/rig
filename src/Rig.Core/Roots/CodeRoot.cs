namespace Rig.Core.Roots;

/// <summary>
/// Where the code root is: the folder every repo is cloned into. <c>~/code</c> is only the last-resort default; the
/// layout under the root (<c>personal/</c>, then a folder per org) is what rig relies on. Resolution order:
/// <c>CODE_ROOT</c>; the nearest ancestor of the start folder carrying the marker, an <c>AGENTS.md</c> beside a
/// <c>personal/</c> folder (which still finds the root from a worktree nested in a repo); the root the running rig
/// checkout implies, since it is cloned at <c>&lt;root&gt;/personal/rig</c>; then <c>~/code</c>, else
/// <c>&lt;drive&gt;/code</c>. These are pure helpers: <see cref="EstateLayout"/> gathers the inputs and resolves once.
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

    /// <summary>
    /// The code root a rig checkout implies: two folders up when the checkout sits in a <c>personal/</c> folder
    /// (<c>&lt;root&gt;/personal/rig</c>), else null.
    /// </summary>
    public static string? FromCheckout(string? checkout)
    {
        if (string.IsNullOrEmpty(checkout))
        {
            return null;
        }

        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkout)));
        return folder.Parent is { Name: PersonalFolder, Parent: { } root } ? root.FullName : null;
    }

    /// <summary>The fallback: <c>&lt;home&gt;/code</c>, else <c>&lt;drive&gt;/code</c>, else <c>&lt;home&gt;/code</c> anyway.</summary>
    public static string HomeFallback(string home)
    {
        var atHome = Path.Combine(home, "code");
        var onDrive = Path.Combine(Path.GetPathRoot(Path.GetFullPath(home)) ?? home, "code");
        return Directory.Exists(atHome) ? atHome : Directory.Exists(onDrive) ? onDrive : atHome;
    }

    /// <summary><paramref name="root"/>'s <c>personal/</c> folder.</summary>
    public static string Personal(string root) => Path.Join(root, PersonalFolder);

    /// <summary>
    /// The code root, given the <c>CODE_ROOT</c> value (if any), the folder to search up from, the home folder and the
    /// rig checkout this process runs from. A checkout only counts when the root it implies is inside
    /// <paramref name="home"/>: one elsewhere belongs to another home, such as the real one when a test passes its own.
    /// </summary>
    public static string Resolve(string? configured, string start, string home, string? checkout = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (!string.IsNullOrEmpty(configured))
        {
            return ExpandHome(configured, home);
        }

        return Find(start) ?? InsideHome(FromCheckout(checkout), home) ?? HomeFallback(home);
    }

    /// <summary><paramref name="root"/> as people type it: <c>~/…</c> with forward slashes when it is inside <paramref name="home"/>.</summary>
    public static string Display(string root, string home)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(home);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        var shown = PathResolution.IsUnder(full, fullHome) ? "~" + full[fullHome.Length..] : full;
        return shown.Replace('\\', '/');
    }

    private static string? InsideHome(string? root, string home) =>
        root is not null && PathResolution.IsUnder(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(home))) ? root : null;

    private static string ExpandHome(string path, string home) =>
        path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..])
            : path;
}
