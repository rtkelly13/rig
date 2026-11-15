namespace Rig.Specs.StepDefinitions;

/// <summary>
/// A fresh scratch folder for one scenario, deleted when the scenario ends. It lives under the checkout's
/// <c>temp/rig-specs</c> as the repository conventions require, not in the system temp directory.
/// </summary>
public sealed class SpecScratch : IDisposable
{
    /// <summary>Creates the folder.</summary>
    public SpecScratch() => _ = Directory.CreateDirectory(Path);

    /// <summary>The folder's absolute path.</summary>
    public string Path { get; } = System.IO.Path.Combine(RepositoryRoot(), "temp", "rig-specs", Guid.NewGuid().ToString("N"));

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
            // A process under test may still hold a file open; the next run or the OS cleans up.
        }
    }

    // The checkout root: the nearest ancestor of the test assembly carrying rig.slnx. A test assembly copied outside
    // the checkout has no repository temp, so the system temp is the only honest place left.
    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(AppContext.BaseDirectory); candidate is not null; candidate = candidate.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(candidate.FullName, "rig.slnx")))
            {
                return candidate.FullName;
            }
        }

        return System.IO.Path.GetTempPath();
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
            // Left for the next run or the OS cleaner.
        }
    }
}
