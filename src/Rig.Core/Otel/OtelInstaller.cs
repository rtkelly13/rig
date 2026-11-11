using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Rig.Core.Otel;

/// <summary>A download whose sha256 is not the pinned one, or an archive without the expected binary.</summary>
public sealed class DigestMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DigestMismatchException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public DigestMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and its cause.</summary>
    public DigestMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Downloads, verifies, then unpacks the pinned binaries. Ported from the install halves of <c>rig.otel_stack.collector</c>
/// and <c>rig.otel_stack.pinned</c>. Nothing is unpacked before the download's sha256 matches the pin, only the one named
/// regular file is taken from an archive, and a <c>.sha256</c> marker beside the binary (the pin and a newline, as the
/// Python wrote it) makes a verified install a no-op.
/// </summary>
/// <param name="http">Follows redirects (release assets redirect to a CDN) but is only ever given an https URL.</param>
public sealed class OtelInstaller(HttpClient http)
{
    private const string Marker = ".sha256";

    /// <summary>Whether <paramref name="binary"/> in <paramref name="folder"/> came from a download matching <paramref name="sha256"/>.</summary>
    public static bool Verified(string binary, string folder, string sha256)
    {
        var marker = Path.Join(folder, Marker);
        return File.Exists(binary) && File.Exists(marker) && File.ReadAllText(marker, Encoding.UTF8).Trim() == sha256;
    }

    /// <summary>Whether the core collector for <paramref name="key"/> is installed and verified under <paramref name="home"/>.</summary>
    public static bool CollectorVerified(string home, string key) =>
        Verified(OtelPins.BinaryPath(home, key), OtelPins.InstallDir(home), OtelPins.Releases[key].Sha256);

    /// <summary>Whether <paramref name="pin"/> is installed and verified under <paramref name="home"/>.</summary>
    public static bool PinVerified(Pin pin, string home) => Verified(OtelPins.PinBinary(pin, home), OtelPins.PinDir(pin, home), pin.Sha256);

    /// <summary>The lower-case hex sha256 of the file at <paramref name="path"/>.</summary>
    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Installs the core collector for <paramref name="key"/> under <paramref name="home"/>; returns its binary.</summary>
    /// <exception cref="DigestMismatchException">The archive is not the pinned one, or lacks the binary.</exception>
    public Task<string> InstallCollectorAsync(string home, string key, CancellationToken cancellationToken = default)
    {
        var release = OtelPins.Releases[key];
        var url = new Uri($"{OtelPins.ReleasesUrl}/v{OtelPins.Version}/{release.Asset}");
        return InstallAsync(
            url,
            release.Sha256,
            OtelPins.InstallDir(home),
            OtelPins.BinaryPath(home, key),
            $"{release.Asset}.part",
            release.Binary,
            actual => $"{release.Asset}: sha256 {actual}, pinned {release.Sha256}; nothing was extracted",
            cancellationToken);
    }

    /// <summary>Installs <paramref name="pin"/> under <paramref name="home"/>; returns its binary.</summary>
    /// <exception cref="DigestMismatchException">The download is not the pinned one, or its archive lacks the binary.</exception>
    public Task<string> InstallAsync(Pin pin, string home, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return InstallAsync(
            pin.Url,
            pin.Sha256,
            OtelPins.PinDir(pin, home),
            OtelPins.PinBinary(pin, home),
            $"{pin.Binary}.download",
            pin.Member,
            actual => $"{pin.Url.OriginalString}: sha256 {actual}, pinned {pin.Sha256}; nothing was unpacked",
            cancellationToken);
    }

    /// <summary>Streams <paramref name="url"/> to <paramref name="destination"/>. HTTPS only.</summary>
    /// <exception cref="ArgumentException">The URL is not https.</exception>
    public async Task DownloadAsync(Uri url, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"refusing non-HTTPS download: {url.OriginalString}", nameof(url));
        }

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var file = File.Create(destination);
            await using (file.ConfigureAwait(false))
            {
                await body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Downloads `url` into `folder` as `download`, then (only once its sha256 is `sha256`) unpacks `member` from it to
    // `target`, or moves it there whole when there is no member.
    private async Task<string> InstallAsync(
        Uri url,
        string sha256,
        string folder,
        string target,
        string download,
        string member,
        Func<string, string> mismatch,
        CancellationToken cancellationToken)
    {
        if (Verified(target, folder, sha256))
        {
            return target;
        }

        _ = Directory.CreateDirectory(folder);
        var partial = Path.Join(folder, download);
        try
        {
            await DownloadAsync(url, partial, cancellationToken).ConfigureAwait(false);
            var actual = Sha256Of(partial);
            if (actual != sha256)
            {
                throw new DigestMismatchException(mismatch(actual));
            }

            if (member.Length > 0)
            {
                await UnpackAsync(partial, member, target, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Place(partial, target);
            }
        }
        finally
        {
            File.Delete(partial);
        }

        WriteMarker(folder, sha256);
        return target;
    }

    // Writes only `member` of the gzipped tarball to `target`; nothing else in it is touched.
    private static async Task UnpackAsync(string archive, string member, string target, CancellationToken cancellationToken)
    {
        var partial = target + ".tmp";
        var source = File.OpenRead(archive);
        await using (source.ConfigureAwait(false))
        {
            var gzip = new GZipStream(source, CompressionMode.Decompress);
            await using (gzip.ConfigureAwait(false))
            {
                var reader = new TarReader(gzip);
                await using (reader.ConfigureAwait(false))
                {
                    // Streamed, never buffered: each match is copied out as it is read, overwriting an earlier one, so
                    // the last entry of that name wins (as tarfile's getmember) in memory bounded by the copy buffer.
                    var regular = false;
                    while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
                    {
                        if (entry.Name != member)
                        {
                            continue;
                        }

                        regular = entry is { EntryType: TarEntryType.RegularFile or TarEntryType.V7RegularFile, DataStream: not null };
                        if (regular)
                        {
                            var output = File.Create(partial);
                            await using (output.ConfigureAwait(false))
                            {
                                await entry.DataStream!.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }

                    if (!regular)
                    {
                        File.Delete(partial);
                        throw new DigestMismatchException($"{Path.GetFileName(archive)} has no regular file {member}");
                    }
                }
            }
        }

        Place(partial, target);
    }

    // Makes `source` executable and moves it over `target` in one step.
    private static void Place(string source, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(source, (UnixFileMode)0b111_101_101);
        }

        File.Move(source, target, overwrite: true);
    }

    private static void WriteMarker(string folder, string sha256) =>
        File.WriteAllText(Path.Join(folder, Marker), sha256 + "\n", new UTF8Encoding(false));
}
