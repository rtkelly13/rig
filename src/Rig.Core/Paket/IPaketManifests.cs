namespace Rig.Core.Paket;

/// <summary>One package a Paket file names. <paramref name="Version"/> is null in a <c>paket.references</c>, which names none.</summary>
/// <param name="Name">The package id as written.</param>
/// <param name="Version">The locked version, else the constraint as Paket renders it; empty when unconstrained.</param>
public sealed record PaketPackage(string Name, string? Version);

/// <summary>The packages one Paket group names; the default group is <c>Main</c>.</summary>
public sealed record PaketGroup(string Name, IReadOnlyList<PaketPackage> Packages);

/// <summary>What one Paket file says, or why it could not be read.</summary>
/// <param name="Path">The file as it was asked for.</param>
/// <param name="Groups">Its groups; empty when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the file could not be read; null when it was.</param>
/// <param name="Warning">A problem that cost accuracy but not the file, such as an unreadable <c>paket.lock</c>.</param>
public sealed record PaketFile(string Path, IReadOnlyList<PaketGroup> Groups, string? Error, string? Warning);

/// <summary>The answer for one batch of Paket files.</summary>
/// <param name="Files">One entry per requested file, in request order; empty when <paramref name="Unavailable"/> is set.</param>
/// <param name="Unavailable">Why no file could be read at all, such as a missing sidecar; null when the batch ran.</param>
public sealed record PaketReading(IReadOnlyList<PaketFile> Files, string? Unavailable = null);

/// <summary>
/// Reads <c>paket.dependencies</c> and <c>paket.references</c> files with Paket's own parsers. Paket.Core stays out of
/// rig's process behind this port: the real reader is the <c>Rig.Paket</c> sidecar (<see cref="PaketSidecar"/>).
/// </summary>
public interface IPaketManifests
{
    /// <summary>
    /// Reads <paramref name="paths"/> (absolute paths of <c>paket.dependencies</c> and <c>paket.references</c> files) in one
    /// batch. A <c>paket.dependencies</c> takes its resolved versions from the <c>paket.lock</c> beside it. A file that
    /// cannot be read is an entry with an error, not a failure; a reader that cannot run at all says so in
    /// <see cref="PaketReading.Unavailable"/>.
    /// </summary>
    Task<PaketReading> ReadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}
