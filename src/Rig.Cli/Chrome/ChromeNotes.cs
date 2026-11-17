using System.Diagnostics;

namespace Rig.Cli.Chrome;

/// <summary>
/// The note an unattended organise leaves when the user's input stops grouping: written to
/// <c>&lt;temp&gt;/workspace-manager/&lt;file&gt;</c> and opened in Notepad on Windows. Elsewhere it is only written.
/// Ported from <c>desktop_manager.restore_fallbacks.open_note</c>.
/// </summary>
/// <param name="Directory">The folder notes are written to.</param>
/// <param name="Open">Shows a written note to the user.</param>
internal sealed record ChromeNotes(string Directory, Action<string> Open)
{
    /// <summary>Notes in the temp folder, opened in Notepad on Windows.</summary>
    public static ChromeNotes Default { get; } = new(Path.Join(Path.GetTempPath(), "workspace-manager"), OpenInNotepad);

    /// <summary>
    /// A writer for one run: every call rewrites the note, but only the first opens it, so a run interrupted twice does
    /// not stack up windows.
    /// </summary>
    public Action<string, IReadOnlyList<string>> Once()
    {
        var opened = false;
        return (file, lines) =>
        {
            var path = Write(file, lines);
            if (!opened)
            {
                opened = true;
                Open(path);
            }
        };
    }

    private string Write(string file, IReadOnlyList<string> lines)
    {
        _ = System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Join(Directory, file);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }

    private static void OpenInNotepad(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var notepad = Path.Join(Environment.GetEnvironmentVariable("SYSTEMROOT") ?? @"C:\Windows", "System32", "notepad.exe");
        var start = new ProcessStartInfo(notepad) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            // A note that cannot be shown must not stop the run; it is still on disk.
        }
    }
}
