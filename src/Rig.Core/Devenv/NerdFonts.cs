using System.IO.Compression;

namespace Rig.Core.Devenv;

/// <summary>A Nerd Font family and the release zip that ships it.</summary>
/// <param name="Name">The family's name, which its font files carry.</param>
/// <param name="Zip">The release asset holding it.</param>
public sealed record NerdFont(string Name, string Zip);

/// <summary>
/// The developer Nerd Fonts: which are in the user font folder, and installing the rest from the latest
/// <c>ryanoasis/nerd-fonts</c> release. Ported from devenv's <c>fonts</c> and <c>downloads</c>.
/// </summary>
/// <param name="http">Downloads the release zips.</param>
public sealed class NerdFonts(HttpClient http)
{
    /// <summary>Where the release zips are downloaded from.</summary>
    public const string ReleaseBase = "https://github.com/ryanoasis/nerd-fonts/releases/latest/download";

    /// <summary>The user agent the downloads send.</summary>
    public const string UserAgent = "shared-utilities/devenv";

    private static readonly string[] Patterns = ["*.ttf", "*.otf"];

    /// <summary>Every family devenv installs, in order.</summary>
    public static readonly IReadOnlyList<NerdFont> All =
    [
        new("JetBrainsMono Nerd Font", "JetBrainsMono.zip"),
        new("CascadiaCode Nerd Font", "CascadiaCode.zip"),
        new("FiraCode Nerd Font", "FiraCode.zip"),
    ];

    /// <summary>The user font folder under <paramref name="home"/> on Windows, macOS or (otherwise) Linux.</summary>
    public static string Folder(string home, bool windows, bool macOS) =>
        windows ? Path.Join(home, "AppData", "Local", "Microsoft", "Windows", "Fonts")
        : macOS ? Path.Join(home, "Library", "Fonts")
        : Path.Join(home, ".local", "share", "fonts");

    /// <summary>Whether <paramref name="folder"/> holds a <c>.ttf</c> or <c>.otf</c> whose name, spaces aside, contains <paramref name="font"/>'s.</summary>
    public static bool Installed(string folder, NerdFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var name = Squash(font.Name);
        return Patterns
            .SelectMany(pattern => Directory.EnumerateFiles(folder, pattern))
            .Any(file => Squash(Path.GetFileName(file)).Contains(name, StringComparison.Ordinal));
    }

    /// <summary>The release zip's URL for <paramref name="font"/>.</summary>
    public static Uri Url(NerdFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        return new Uri($"{ReleaseBase}/{font.Zip}");
    }

    /// <summary>
    /// Downloads <paramref name="font"/>'s zip and copies its <c>.ttf</c> and <c>.otf</c> files, except the
    /// "Windows Compatible" ones, flat into <paramref name="folder"/>. Returns null, or why it could not.
    /// </summary>
    public async Task<string?> InstallAsync(NerdFont font, string folder, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(font));
            _ = request.Headers.UserAgent.TryParseAdd(UserAgent);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var zip = await ZipArchive.CreateAsync(stream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null, cancellationToken).ConfigureAwait(false);
                await using (zip.ConfigureAwait(false))
                {
                    _ = Directory.CreateDirectory(folder);
                    foreach (var entry in zip.Entries.Where(Wanted))
                    {
                        // The file name alone: a zip entry never chooses where outside the font folder it lands.
                        await entry.ExtractToFileAsync(Path.Join(folder, Path.GetFileName(entry.FullName)), overwrite: true, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            return e.Message;
        }
    }

    private static bool Wanted(ZipArchiveEntry entry) =>
        (entry.FullName.EndsWith(".ttf", StringComparison.Ordinal) || entry.FullName.EndsWith(".otf", StringComparison.Ordinal))
        && !entry.FullName.Contains("Windows Compatible", StringComparison.Ordinal);

    private static string Squash(string text) => text.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
