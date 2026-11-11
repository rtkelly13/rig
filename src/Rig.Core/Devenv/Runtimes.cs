using System.Text.RegularExpressions;

namespace Rig.Core.Devenv;

/// <summary>Which Python <c>rig devenv runtimes</c> asks uv for. Ported from devenv's <c>steps/runtimes.py</c>.</summary>
public static partial class Runtimes
{
    /// <summary>The Python uv installs when the folder names none.</summary>
    public const string DefaultPython = "3.13";

    /// <summary>
    /// The folder's <c>.python-version</c>, else the first <c>major.minor</c> in its <c>pyproject.toml</c>'s
    /// <c>requires-python</c>, else <see cref="DefaultPython"/>.
    /// </summary>
    public static string PythonFor(string folder)
    {
        var pinned = Read(Path.Join(folder, ".python-version"))?.Trim();
        if (!string.IsNullOrEmpty(pinned))
        {
            return pinned;
        }

        var pyproject = Read(Path.Join(folder, "pyproject.toml"));
        return pyproject is not null
            && RequiresPython().Match(pyproject) is { Success: true } requires
            && MajorMinor().Match(requires.Groups[1].Value) is { Success: true } version
            ? version.Value
            : DefaultPython;
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("""requires-python\s*=\s*["']([^"']+)["']""")]
    private static partial Regex RequiresPython();

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex MajorMinor();
}
