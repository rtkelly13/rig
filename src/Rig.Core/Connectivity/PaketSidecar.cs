using System.Text.Json;
using Rig.Core.Processes;

namespace Rig.Core.Connectivity;

/// <summary>
/// An <see cref="IPaketManifests"/> that runs the <c>Rig.Paket</c> sidecar once per batch: it takes the paths as a JSON array on
/// standard input and answers with JSON on standard output, both ASCII so no code page can garble a path. The sidecar is the only place Paket.Core, FSharp.Core and
/// Newtonsoft.Json load, so none of them reach rig itself.
/// </summary>
/// <param name="proc">How the sidecar is run.</param>
/// <param name="executable">The sidecar's path; <see cref="DefaultPath"/> unless a test points elsewhere.</param>
public sealed class PaketSidecar(IProc proc, string? executable = null) : IPaketManifests
{
    /// <summary>The folder, beside rig's own binaries, the sidecar lives in: its dependencies stay out of rig's.</summary>
    public const string Folder = "paket";

    /// <summary>How long the sidecar may take for a whole repo before it is stopped.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string _executable = executable ?? DefaultPath;

    /// <summary>The sidecar beside the running rig: <c>paket/Rig.Paket</c> under the application folder.</summary>
    public static string DefaultPath => Path.Join(AppContext.BaseDirectory, Folder, FileName(OperatingSystem.IsWindows()));

    /// <summary>The sidecar's file name: an <c>.exe</c> on Windows.</summary>
    public static string FileName(bool windows) => windows ? "Rig.Paket.exe" : "Rig.Paket";

    /// <inheritdoc/>
    public async Task<PaketReading> ReadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(_executable))
        {
            return Unavailable($"the Paket sidecar is not installed at {_executable}");
        }

        var ran = await proc.RunAsync(
            new ProcRequest([_executable]) { StdinText = JsonSerializer.Serialize(paths), Timeout = Timeout },
            cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            var why = (ran.Err.Length > 0 ? ran.Err : ran.Out).Trim();
            return Unavailable($"the Paket sidecar exited with {ran.Code}: {why}");
        }

        try
        {
            return Parse(ran.Out);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Unavailable($"the Paket sidecar answered something unreadable: {e.Message}");
        }
    }

    private static PaketReading Unavailable(string reason) => new([], reason);

    private static PaketReading Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var files = document.RootElement.GetProperty("files").EnumerateArray().Select(file => new PaketFile(
            file.GetProperty("path").GetString()!,
            [.. file.GetProperty("groups").EnumerateArray().Select(group => new PaketGroup(
                group.GetProperty("name").GetString()!,
                [.. group.GetProperty("packages").EnumerateArray().Select(package => new PaketPackage(
                    package.GetProperty("name").GetString()!,
                    package.GetProperty("version").GetString()))]))],
            file.GetProperty("error").GetString(),
            file.GetProperty("warning").GetString()));
        return new PaketReading([.. files]);
    }
}
