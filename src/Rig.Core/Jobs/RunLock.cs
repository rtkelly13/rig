using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace Rig.Core.Jobs;

/// <summary>
/// The coordinator's <c>run.lock</c>: one run at a time on Windows, where the lock is byte 0 of the file, the same byte
/// Python's <c>msvcrt.locking</c> takes, so a C# run and a Python run exclude each other. Elsewhere there is no
/// cross-process lock, as in Python, and every run gets it. Ported from <c>desktop_manager.display_guard.hold_lock</c>.
/// </summary>
public static class RunLock
{
    /// <summary>How long a run waits between tries for a held lock.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Takes the lock at <paramref name="path"/>, trying again every <see cref="Step"/> for <paramref name="wait"/>.
    /// What to dispose to let it go, or null when another run still holds it.
    /// </summary>
    /// <param name="path">The lock file; made, holding one byte, when it is missing or empty.</param>
    /// <param name="wait">How long to keep trying; zero tries once.</param>
    /// <param name="sleep">Waits one step between tries.</param>
    public static IDisposable? Hold(string path, TimeSpan wait, Action<TimeSpan> sleep)
    {
        ArgumentNullException.ThrowIfNull(sleep);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            stream.Position = 0;
            stream.WriteByte((byte)'0');
            stream.Flush();
        }

        return OperatingSystem.IsWindows() ? LockByte(stream, wait, sleep) : stream;
    }

    [ExcludeFromCodeCoverage(Justification = "Windows byte-range lock; the Windows CI job tests it, the ubuntu coverage run cannot reach it.")]
    [SupportedOSPlatform("windows")]
    private static Held? LockByte(FileStream stream, TimeSpan wait, Action<TimeSpan> sleep)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            try
            {
                stream.Lock(0, 1);
                return new Held(stream);
            }
            catch (IOException) when (waited < wait)
            {
                sleep(Step);
                waited += Step;
            }
            catch (IOException)
            {
                stream.Dispose();
                return null;
            }
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows byte-range lock; the Windows CI job tests it, the ubuntu coverage run cannot reach it.")]
    [SupportedOSPlatform("windows")]
    private sealed class Held(FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                stream.Unlock(0, 1);
            }
            catch (IOException)
            {
                // Closing the handle below lets the lock go anyway.
            }

            stream.Dispose();
        }
    }
}
