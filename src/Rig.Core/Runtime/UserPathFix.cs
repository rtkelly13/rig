using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Runtime;

/// <summary>
/// The user's own PATH, as Windows keeps it (<c>HKCU\Environment\Path</c>): the raw value, with any
/// <c>%VARIABLE%</c> left unexpanded so writing it back keeps them.
/// </summary>
public interface IUserPath
{
    /// <summary>The raw value, or null when the user has none.</summary>
    string? Read();

    /// <summary>Replaces the value, and tells running programs the environment changed.</summary>
    /// <param name="value">The new raw value.</param>
    void Write(string value);
}

/// <summary>What <see cref="UserPathFix.Plan"/> would change.</summary>
/// <param name="Value">The new raw User PATH, or null when nothing changes.</param>
/// <param name="Changes">Each change in a sentence, in the order made.</param>
public sealed record UserPathPlan(string? Value, IReadOnlyList<string> Changes);

/// <summary>
/// The safe fixes <c>rig runtime install</c> makes to the Windows User PATH so <c>rig</c> is the installed copy: take
/// off every Python virtualenv folder holding a <c>rig</c> (the old Python CLI), and put the shim's folder first when
/// neither it nor the runtime's <c>current</c> copy is on it. Every other entry is kept as written; only empty ones are
/// dropped. The Machine PATH, shell profiles and other copies are only reported, by <see cref="RigCopies"/>.
/// </summary>
public static class UserPathFix
{
    // The registry value is a Windows PATH whatever platform reads it.
    private const char Separator = ';';

    /// <summary>The changes to <paramref name="userPath"/>. Pure apart from looking at the folders it names.</summary>
    /// <param name="userPath">The raw User PATH, or null.</param>
    /// <param name="expand">Expands an entry's <c>%VARIABLE%</c>s, so it can be compared and looked in.</param>
    /// <param name="extensions">The <c>PATHEXT</c> value, to find a venv's <c>rig</c>.</param>
    /// <param name="home">The home folder the shim lives under.</param>
    /// <param name="runtimeRoot">The runtime folder.</param>
    public static UserPathPlan Plan(string? userPath, Func<string, string> expand, string? extensions, string home, string runtimeRoot)
    {
        ArgumentNullException.ThrowIfNull(expand);
        var changes = new List<string>();
        var kept = new List<string>();
        foreach (var entry in (userPath ?? string.Empty).Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var folder = expand(entry).Trim().Trim('"');
            if (RigCopies.PythonVenvScripts(folder) && ProgramPath.FindAll("rig", folder, extensions).Any())
            {
                changes.Add($"Removed {folder.Replace('\\', '/')} from your User PATH: it holds the old Python rig CLI.");
                continue;
            }

            kept.Add(entry);
        }

        var shimFolder = Path.GetDirectoryName(RigShim.PathFor(home, windows: true))!;
        var currentFolder = Path.GetDirectoryName(RuntimeManager.Published(RuntimeManager.CurrentLink(runtimeRoot), "rig", windows: true))!;
        if (!kept.Any(entry => Names(expand(entry), shimFolder) || Names(expand(entry), currentFolder)))
        {
            kept.Insert(0, shimFolder);
            changes.Add($"Added {shimFolder.Replace('\\', '/')} to the front of your User PATH, so `rig` runs the shim.");
        }

        var value = string.Join(Separator, kept);
        return new UserPathPlan(changes.Count == 0 ? null : value, changes);
    }

    // Whether a PATH entry names `folder`, trailing separators, quotes and case aside.
    private static bool Names(string entry, string folder)
    {
        var trimmed = entry.Trim().Trim('"');
        return trimmed.Length > 0
            && PathResolution.Same(Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
    }
}
