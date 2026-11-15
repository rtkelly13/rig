namespace Rig.Specs.StepDefinitions;

/// <summary>A fresh folder under the system temp directory, deleted when the scenario ends.</summary>
public sealed class SpecScratch : IDisposable
{
    /// <summary>Creates the folder.</summary>
    public SpecScratch() => _ = Directory.CreateDirectory(Path);

    /// <summary>The folder's absolute path.</summary>
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rig-specs", Guid.NewGuid().ToString("N"));

    /// <summary>Creates <paramref name="relative"/> as a folder and returns its full path.</summary>
    public string Folder(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

    /// <summary>Creates <paramref name="relative"/> with <paramref name="content"/> and returns its full path.</summary>
    public string Write(string relative, string content)
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Reads <paramref name="relative"/>.</summary>
    public string Read(string relative) => File.ReadAllText(System.IO.Path.Combine(Path, relative));

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // git writes its objects read-only on Windows; make them writable and try once more.
            MakeWritable();
            TryDelete();
        }
        catch (IOException)
        {
            // A process under test may still hold a file open; the OS temp cleaner will get it.
        }
    }

    private void MakeWritable()
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left for the OS temp cleaner.
        }
    }
}
