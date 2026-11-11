using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Rig.Core.Roots;

namespace Rig.Core.Skills;

/// <summary>Makes a directory link: the one step .NET cannot do alone on Windows without the symlink privilege.</summary>
public interface IDirectoryLinker
{
    /// <summary>Points <paramref name="link"/> at the existing directory <paramref name="target"/>.</summary>
    /// <exception cref="IOException">Neither a symlink nor a junction could be made.</exception>
    void Link(string link, string target);
}

/// <summary>
/// A symlink, else on Windows a directory junction, which needs neither Developer Mode nor elevation. .NET has no
/// junction API, so the junction comes from <c>mklink /J</c>.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "The operating-system edge; skills tests use a fake that refuses links.")]
public sealed class SystemDirectoryLinker : IDirectoryLinker
{
    /// <inheritdoc/>
    public void Link(string link, string target)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, Path.GetFullPath(target) })
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start) ?? throw new IOException($"Could not start mklink for {link}.", e);
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new IOException($"Neither a symlink nor a junction could be made at {link}.", e);
            }
        }
    }
}

/// <summary>
/// Reading and removing directory links, symlinks and Windows junctions alike, dangling or not. Removing a link never
/// touches what it points at; only a real directory that is not a link is ever deleted with its contents.
/// </summary>
public static class DirectoryLinks
{
    /// <summary>Whether <paramref name="path"/> is a symlink or a junction, dangling or not.</summary>
    public static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>Whether anything, a dangling link included, occupies <paramref name="path"/>.</summary>
    public static bool Occupied(string path) => IsLink(path) || Path.Exists(path);

    /// <summary>Where the link at <paramref name="path"/> leads with every link followed, or null when that does not exist.</summary>
    public static string? Resolve(string path)
    {
        var resolved = PathResolution.Resolve(path);
        return Path.Exists(resolved) ? resolved : null;
    }

    /// <summary>Whether two resolved paths name the same place: ignoring case on Windows, as Python's paths compare there.</summary>
    public static bool Same(string left, string right, bool windows) =>
        string.Equals(left, right, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Removes the link at <paramref name="path"/> and only the link.</summary>
    public static void RemoveLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
        {
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    /// <summary>Removes whatever is at <paramref name="path"/>: a link alone, a file, or a real directory and its contents.</summary>
    public static void Remove(string path)
    {
        if (IsLink(path))
        {
            RemoveLink(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Copies the directory <paramref name="source"/> to <paramref name="destination"/>, following links as
    /// <c>shutil.copytree</c> does by default, and leaving out each entry whose name <paramref name="skip"/> accepts.
    /// </summary>
    public static void Copy(string source, string destination, Func<string, bool>? skip = null)
    {
        _ = Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (skip?.Invoke(name) == true)
            {
                continue;
            }

            // A link is never followed: an upstream clone is untrusted, and a link could copy in a local file or loop.
            if (IsLink(entry))
            {
                continue;
            }

            var target = Path.Join(destination, name);
            if (Directory.Exists(entry))
            {
                Copy(entry, target, skip);
            }
            else
            {
                File.Copy(entry, target);
            }
        }
    }
}
