using System.Text.Json;

namespace Rig.Core.Desktop;

/// <summary>
/// Tell apart windows of one executable by the pid an app records for each instance. Ported from
/// <c>desktop_manager.window_pid_file</c>.
/// </summary>
/// <remarks>
/// Orca runs one window per profile, and every profile's window is <c>Orca.exe</c> titled "Orca", so title, process and
/// position cannot say which profile a window belongs to. Each running profile writes <c>orca-runtime.json</c> with the
/// <c>pid</c> of the process that owns the window. An entry's <see cref="LaunchEntry.WindowPidFile"/> names that file:
/// a window qualifies only when its owner is the recorded pid. A missing or unreadable file means the instance is not
/// running, so no window qualifies and restore launches it.
/// </remarks>
public static class WindowPidFile
{
    /// <summary>The integer <c>pid</c> in the JSON object at <paramref name="path"/>, or null when it is missing or malformed.</summary>
    public static int? RecordedPid(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("pid", out var pid)
                && pid.ValueKind == JsonValueKind.Number
                && pid.GetRawText().TrimStart('-').All(char.IsAsciiDigit) // Python's StrictInt takes no 1.0 or 1e3.
                && pid.TryGetInt32(out var value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>False when <paramref name="entry"/> pins its window to a recorded pid and <paramref name="windowPid"/> is not it.</summary>
    public static bool WindowOwnerAllowed(LaunchEntry entry, int? windowPid, IReadOnlyDictionary<string, string> substitutions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrEmpty(entry.WindowPidFile))
        {
            return true;
        }

        var resolved = Placeholders.Substitute(entry.WindowPidFile, substitutions);
        var expected = RecordedPid(resolved.Length > 0 ? resolved : entry.WindowPidFile);
        return expected is not null && windowPid == expected;
    }

    /// <summary>
    /// True when <paramref name="entry"/> pins its window to a recorded pid and <paramref name="windowPid"/> is that one.
    /// Such a window is the entry's own instance wherever it sits, so restore may claim it from another desktop and move
    /// it rather than launch a single-instance app again.
    /// </summary>
    public static bool PidVerified(LaunchEntry entry, int? windowPid, IReadOnlyDictionary<string, string> substitutions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !string.IsNullOrEmpty(entry.WindowPidFile) && WindowOwnerAllowed(entry, windowPid, substitutions);
    }
}
