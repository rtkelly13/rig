using System.Text;

namespace Rig.Core.Runtime;

/// <summary>What happened to the <c>rig</c> shim on PATH.</summary>
public enum ShimOutcome
{
    /// <summary>Rig wrote the shim.</summary>
    Written,

    /// <summary>The shim was already exactly what rig writes.</summary>
    Unchanged,

    /// <summary>The file belongs to something else, so rig left it alone.</summary>
    Foreign,
}

/// <summary>
/// The <c>rig</c> command on PATH, pointing at the runtime's <c>current</c> link so later installs and rollbacks move it
/// with no rewrite. Ported from <c>desktop_manager.rig_shim</c>: a file at that path rig did not write is left alone.
/// </summary>
public static class RigShim
{
    /// <summary>In every shim rig writes; a file without it belongs to someone else.</summary>
    public const string Marker = "Written by `desktop-manager runtime install`";

    /// <summary>The shim's path: <c>~/.local/bin/rig</c>, or <c>rig.cmd</c> on Windows.</summary>
    /// <param name="home">The user's home folder.</param>
    /// <param name="windows">Whether to name the Windows <c>.cmd</c> shim.</param>
    public static string PathFor(string home, bool windows) =>
        Path.Join(home, ".local", "bin", windows ? "rig.cmd" : "rig");

    /// <summary>The shim's text: run <paramref name="target"/> with every argument passed through.</summary>
    /// <param name="target">The executable the shim runs.</param>
    /// <param name="windows">Whether to render the Windows <c>.cmd</c> shim.</param>
    public static string Render(string target, bool windows)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (windows)
        {
            return $"@echo off\r\nrem {Marker}. Runs rig from the runtime copy.\r\n\"{target}\" %*\r\n";
        }

        var quoted = target.Replace("'", "'\\''", StringComparison.Ordinal);
        return $"#!/bin/sh\n# {Marker}. Runs rig from the runtime copy.\nexec '{quoted}' \"$@\"\n";
    }

    /// <summary>
    /// Writes the shim at <paramref name="shim"/> to run rig from <paramref name="root"/>'s <c>current</c> copy, unless
    /// the path holds a file rig did not write.
    /// </summary>
    /// <param name="shim">The shim's path.</param>
    /// <param name="root">The runtime folder holding <c>current</c>.</param>
    /// <param name="windows">Whether the shim is the Windows <c>.cmd</c> one.</param>
    public static ShimOutcome Install(string shim, string root, bool windows)
    {
        ArgumentNullException.ThrowIfNull(shim);
        var text = Render(RuntimeManager.Executable(RuntimeManager.CurrentLink(root), "rig", windows), windows);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        if (File.Exists(shim))
        {
            var existing = File.ReadAllBytes(shim);
            if (existing.AsSpan().IndexOf(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Marker)) < 0)
            {
                return ShimOutcome.Foreign;
            }

            if (existing.AsSpan().SequenceEqual(bytes))
            {
                return ShimOutcome.Unchanged;
            }
        }

        if (Path.GetDirectoryName(shim) is { Length: > 0 } parent)
        {
            _ = Directory.CreateDirectory(parent);
        }

        var temp = shim + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, (UnixFileMode)0b111_101_101);
            }
            File.Move(temp, shim, overwrite: true);
            return ShimOutcome.Written;
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
