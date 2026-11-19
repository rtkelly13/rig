using Paket;

namespace Rig.Paket;

/// <summary>One package a Paket file names. <see cref="Version"/> is null for a references file, which names none.</summary>
internal sealed record PackageEntry(string Name, string? Version);

internal sealed record GroupEntry(string Name, IReadOnlyList<PackageEntry> Packages);

/// <summary>What one Paket file says, or why it could not be read. A <see cref="Warning"/> still carries groups.</summary>
internal sealed record FileReading(string Path, string Kind, IReadOnlyList<GroupEntry> Groups, string? Error, string? Warning);

/// <summary>Reads <c>paket.dependencies</c>, <c>paket.references</c> and the <c>paket.lock</c> beside a dependencies file with Paket's own parsers.</summary>
internal static class PaketFileReader
{
    /// <summary>The largest file read, in bytes; the same bound rig applies to every manifest.</summary>
    internal const long MaxBytes = 2_000_000;

    internal static FileReading Read(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var kind = name switch
        {
            "paket.dependencies" => "dependencies",
            "paket.references" => "references",
            _ => "unknown",
        };

        try
        {
            return kind switch
            {
                "dependencies" => ReadDependencies(path),
                "references" => ReadReferences(path),
                _ => Failed(path, kind, $"Not a Paket file: {name}"),
            };
        }
#pragma warning disable CA1031 // Paket fails a malformed file with whichever exception type its parser reached for; the file is the problem either way.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return Failed(path, kind, e.Message);
        }
    }

    private static FileReading ReadDependencies(string path)
    {
        var dependencies = DependenciesFile.FromSource(path, ReadBounded(path));
        var (resolved, warning) = ReadLock(path);
        var groups = dependencies.Groups
            .OrderBy(group => group.Key.Name, StringComparer.Ordinal)
            .Select(group => new GroupEntry(
                group.Key.Name,
                [.. group.Value.Packages
                    .Where(package => package.Kind.IsPackage)
                    .Select(package => new PackageEntry(
                        package.Name.Name,
                        resolved.GetValueOrDefault((group.Key.CompareString, package.Name.CompareString)) ?? package.VersionRequirement.Range.ToString()))]))
            .ToList();
        return new FileReading(path, "dependencies", groups, null, warning);
    }

    private static (Dictionary<(string Group, string Package), string> Versions, string? Warning) ReadLock(string dependenciesPath)
    {
        var lockPath = System.IO.Path.Join(System.IO.Path.GetDirectoryName(dependenciesPath), "paket.lock");
        if (!File.Exists(lockPath))
        {
            return ([], null);
        }

        try
        {
            var versions = LockFile.Parse(lockPath, Lines(ReadBounded(lockPath))).Groups
                .SelectMany(group => group.Value.Resolution.Select(package => (Group: group.Key.CompareString, Package: package.Key.CompareString, Version: package.Value.Version.AsString)))
                .ToDictionary(entry => (entry.Group, entry.Package), entry => entry.Version);
            return (versions, null);
        }
#pragma warning disable CA1031 // A lock Paket cannot parse costs the resolved versions, not the inventory.
        catch (Exception e)
#pragma warning restore CA1031
        {
            return ([], $"paket.lock was not used: {e.Message}");
        }
    }

    private static FileReading ReadReferences(string path)
    {
        var references = ReferencesFile.FromLines(Lines(ReadBounded(path)));
        var groups = references.Groups
            .OrderBy(group => group.Key.Name, StringComparer.Ordinal)
            .Select(group => new GroupEntry(group.Key.Name, [.. group.Value.NugetPackages.Select(package => new PackageEntry(package.Name.Name, null))]))
            .ToList();
        return new FileReading(path, "references", groups, null, null);
    }

    /// <summary>
    /// Reads at most <see cref="MaxBytes"/> from the opened file, so the bound holds for what is actually read: a symlink
    /// to a device or an endless file fails here instead of growing until the process dies.
    /// </summary>
    private static string ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MaxBytes + 1];
        var total = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (total > MaxBytes)
        {
            throw new InvalidDataException("Manifest exceeds 2 MB");
        }

        using var reader = new StreamReader(new MemoryStream(buffer, 0, total), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Splits like <see cref="File.ReadAllLines(string)"/>: on CR, LF or CRLF, with no entry after a final newline.</summary>
    private static string[] Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static FileReading Failed(string path, string kind, string error) => new(path, kind, [], error, null);
}
