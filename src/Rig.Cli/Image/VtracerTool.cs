using System.Formats.Tar;
using System.IO.Compression;
using Rig.Core.Processes;
using Rig.Image.Pathways;

namespace Rig.Cli.Image;

/// <summary>
/// The pinned VTracer 0.6.4 release run as a process (ADR 0024). The archive is downloaded once and checked against its pinned
/// SHA-256, the binary is extracted and checked against its own, and the binary is checked again before every run.
/// </summary>
/// <param name="proc">Runs the binary.</param>
/// <param name="folder">Where the verified binary is kept; created on demand.</param>
/// <param name="download">Fetches a release archive.</param>
/// <param name="pin">The release to use; defaults to the one for this machine.</param>
internal sealed class VtracerTool(IProc proc, string folder, Func<Uri, CancellationToken, Task<byte[]>> download, VtracerPin? pin = null) : IVtracer
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Downloads with an <see cref="HttpClient"/>.</summary>
    public static async Task<byte[]> Fetch(Uri url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        return await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> TraceAsync(string referencePath, VtracerConfig config, CancellationToken cancellationToken)
    {
        var binary = await EnsureBinaryAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(folder);
        var output = Path.Join(folder, $"trace-{Guid.NewGuid():N}.svg");
        try
        {
            string[] argv = [binary, .. config.Arguments(Path.GetFullPath(referencePath), output)];
            var ran = await proc.RunAsync(new ProcRequest(argv) { Timeout = RunTimeout }, cancellationToken).ConfigureAwait(false);
            if (!ran.Ok)
            {
                throw new InvalidOperationException($"expected vtracer to exit 0, found {ran.Code}: {ran.Err.Trim()}");
            }

            return Vtracer.WithViewBox(await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            File.Delete(output);
        }
    }

    private async Task<string> EnsureBinaryAsync(CancellationToken cancellationToken)
    {
        var release = pin ?? Vtracer.PinForThisMachine();
        var binary = Path.Join(folder, $"vtracer-{Vtracer.Version}-{release.Platform}", release.Entry);
        if (File.Exists(binary))
        {
            Vtracer.VerifySha256(await File.ReadAllBytesAsync(binary, cancellationToken).ConfigureAwait(false), release.BinarySha256, $"the installed {binary}");
            return binary;
        }

        var archive = await download(release.Url, cancellationToken).ConfigureAwait(false);
        Vtracer.VerifySha256(archive, release.ArchiveSha256, $"the archive from {release.Url}");
        var bytes = release.Url.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) ? ReadZipEntry(archive, release.Entry) : ReadTarEntry(archive, release.Entry);
        Vtracer.VerifySha256(bytes, release.BinarySha256, $"{release.Entry} from {release.Url}");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllBytesAsync(binary, bytes, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return binary;
    }

    private static byte[] ReadZipEntry(byte[] archive, string entry)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        var found = zip.Entries.FirstOrDefault(e => e.Name == entry)
            ?? throw new InvalidDataException($"expected the archive to hold {entry}, found {string.Join(", ", zip.Entries.Select(e => e.FullName))}");
        using var stream = found.Open();
        return Copy(stream);
    }

    private static byte[] ReadTarEntry(byte[] archive, string entry)
    {
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var names = new List<string>();
        while (tar.GetNextEntry() is { } found)
        {
            names.Add(found.Name);
            if (found.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(found.Name) == entry && found.DataStream is { } data)
            {
                return Copy(data);
            }
        }

        throw new InvalidDataException($"expected the archive to hold {entry}, found {string.Join(", ", names)}");
    }

    private static byte[] Copy(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
