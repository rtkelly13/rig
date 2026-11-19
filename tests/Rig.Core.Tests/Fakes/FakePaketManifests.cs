using Rig.Core.Connectivity;

namespace Rig.Core.Tests.Fakes;

/// <summary>An <see cref="IPaketManifests"/> that answers from <see cref="Files"/> and records what it was asked.</summary>
public sealed class FakePaketManifests : IPaketManifests
{
    /// <summary>What each file reads as, by full path (separators as this platform writes them); a file not listed reads as an error.</summary>
    public Dictionary<string, PaketFile> Files { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Why nothing can be read, or null when the reader runs.</summary>
    public string? Unavailable { get; set; }

    /// <summary>Whether the reader answers for the files it is asked about; false leaves every request without an entry.</summary>
    public bool Answers { get; set; } = true;

    /// <summary>Every batch of paths asked for, in order: one entry per call.</summary>
    public List<IReadOnlyList<string>> Batches { get; } = [];

    /// <summary>A <c>paket.dependencies</c> reading with the <c>Main</c> group's packages as (name, version).</summary>
    public FakePaketManifests Dependencies(string path, params (string Name, string? Version)[] packages) =>
        Add(new PaketFile(path, [new PaketGroup("Main", [.. packages.Select(p => new PaketPackage(p.Name, p.Version))])], null, null));

    /// <summary>A <c>paket.references</c> reading with the <c>Main</c> group's package names.</summary>
    public FakePaketManifests References(string path, params string[] names) =>
        Add(new PaketFile(path, [new PaketGroup("Main", [.. names.Select(name => new PaketPackage(name, null))])], null, null));

    /// <summary>Adds a reading under its own path.</summary>
    public FakePaketManifests Add(PaketFile file)
    {
        Files[Path.GetFullPath(file.Path)] = file;
        return this;
    }

    /// <inheritdoc/>
    public Task<PaketReading> ReadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        Batches.Add(paths);
        return Task.FromResult(Unavailable is null && !Answers
            ? new PaketReading([])
            : Unavailable is null
            ? new PaketReading([.. paths.Select(path => Files.GetValueOrDefault(Path.GetFullPath(path)) is { } file ? file with { Path = path } : new PaketFile(path, [], "not in the fake", null))])
            : new PaketReading([], Unavailable));
    }
}
