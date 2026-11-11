namespace Rig.Core.Tests;

/// <summary>A fresh folder under the system temp directory, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    /// <summary>Creates the folder.</summary>
    public TempDir() => Directory.CreateDirectory(Path);

    /// <summary>The folder's absolute path.</summary>
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rig-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Creates <paramref name="relative"/> as a folder under this one and returns its path.</summary>
    public string Folder(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

    /// <summary>Creates <paramref name="relative"/> as an empty file under this one and returns its full path, with this platform's separators.</summary>
    public string File(string relative)
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, string.Empty);
        return path;
    }

    /// <summary>Makes this folder a code root: an AGENTS.md beside personal/.</summary>
    public TempDir AsCodeRoot()
    {
        File("AGENTS.md");
        Folder("personal");
        return this;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // Windows refuses to delete a read-only file, and git writes its objects read-only. Links are not entered.
            Writable();
            TryDelete();
        }
        catch (IOException)
        {
            // A process under test may still hold a file open; the OS temp cleaner will get it.
        }
    }

    private void Writable()
    {
        foreach (var file in new DirectoryInfo(Path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }

    private void TryDelete()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the OS temp cleaner.
        }
    }
}
