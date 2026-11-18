using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Rig.Image.Pathways;

/// <summary>VTracer settings for the <c>autotraced</c> pathway; the defaults are the original's.</summary>
public sealed record VtracerConfig
{
    /// <summary>The colour mode.</summary>
    public string ColorMode { get; init; } = "color";

    /// <summary>How shapes stack.</summary>
    public string Hierarchical { get; init; } = "stacked";

    /// <summary>The curve fitting mode.</summary>
    public string Mode { get; init; } = "polygon";

    /// <summary>Speckles smaller than this are dropped.</summary>
    public int FilterSpeckle { get; init; } = 8;

    /// <summary>Colour precision in bits.</summary>
    public int ColorPrecision { get; init; } = 8;

    /// <summary>The gradient step between layers.</summary>
    public int LayerDifference { get; init; } = 10;

    /// <summary>The corner threshold in degrees.</summary>
    public int CornerThreshold { get; init; } = 60;

    /// <summary>The segment length threshold.</summary>
    public double LengthThreshold { get; init; } = 3.5;

    /// <summary>Decimal places in path coordinates.</summary>
    public int PathPrecision { get; init; } = 1;

    /// <summary>The command-line arguments that make the CLI do what the Python binding's call did.</summary>
    public IReadOnlyList<string> Arguments(string input, string output) =>
    [
        "--input", input,
        "--output", output,
        "--colormode", ColorMode,
        "--hierarchical", Hierarchical,
        "--mode", Mode,
        "--filter_speckle", FilterSpeckle.ToString(CultureInfo.InvariantCulture),
        "--color_precision", ColorPrecision.ToString(CultureInfo.InvariantCulture),
        "--gradient_step", LayerDifference.ToString(CultureInfo.InvariantCulture),
        "--corner_threshold", CornerThreshold.ToString(CultureInfo.InvariantCulture),
        "--segment_length", LengthThreshold.ToString("R", CultureInfo.InvariantCulture),
        "--path_precision", PathPrecision.ToString(CultureInfo.InvariantCulture),
    ];
}

/// <summary>Where one platform's VTracer binary comes from and the digests it must match.</summary>
/// <param name="Platform">A name for messages, such as <c>macos-arm64</c>.</param>
/// <param name="Url">The release archive.</param>
/// <param name="ArchiveSha256">The archive's SHA-256, checked after download.</param>
/// <param name="Entry">The binary's name inside the archive.</param>
/// <param name="BinarySha256">The binary's SHA-256, checked before every run.</param>
public sealed record VtracerPin(string Platform, Uri Url, string ArchiveSha256, string Entry, string BinarySha256);

/// <summary>A downloaded VTracer archive or binary does not match the pinned digest.</summary>
public sealed class VtracerChecksumException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VtracerChecksumException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What was checked and what was found.</param>
    public VtracerChecksumException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What was checked and what was found.</param>
    /// <param name="innerException">The cause.</param>
    public VtracerChecksumException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Runs VTracer over a raster and returns its SVG, viewBox added.</summary>
public interface IVtracer
{
    /// <summary>Auto-traces <paramref name="referencePath"/>; deterministic.</summary>
    Task<string> TraceAsync(string referencePath, VtracerConfig config, CancellationToken cancellationToken);
}

/// <summary>The pinned VTracer 0.6.4 release (ADR 0024): its checksums, its flags and the viewBox step, all pure.</summary>
public static partial class Vtracer
{
    /// <summary>The release rig is pinned to.</summary>
    public const string Version = "0.6.4";

    private const string Release = "https://github.com/visioncortex/vtracer/releases/download/" + Version + "/";

    private static readonly VtracerPin MacArm64 = new(
        "macos-arm64",
        new Uri(Release + "vtracer-aarch64-apple-darwin.tar.gz"),
        "4a597fd2df8b961d60620df40a7436109427d86e5c028758e6e8796b02d3d996",
        "vtracer",
        "77e495bbe212448240387fba3b6d8bc62ba20ecfb6f3c22967e51600f1cc6e66");

    private static readonly VtracerPin WindowsX64 = new(
        "windows-x64",
        new Uri(Release + "vtracer-x86_64-pc-windows-msvc.zip"),
        "6b5bc17a6b017129ee40461df254f65d16f3b494c001a8541d41861066b716bf",
        "vtracer.exe",
        "4ad8d35e566cd15caf582063b8349bd082b8fa2bd461e99d116fc63ad8fdeca0");

    private static readonly VtracerPin LinuxX64 = new(
        "linux-x64",
        new Uri(Release + "vtracer-x86_64-unknown-linux-musl.tar.gz"),
        "9290ba0c90e224d6d212836dff5491407c1718bcb72f80b2b5a4a01816df5e40",
        "vtracer",
        "6f31499257076bd94de3e976844cf7ca5643f1e194a2bf0599b13f3719452aec");

    /// <summary>The pins, one per supported platform.</summary>
    public static IReadOnlyList<VtracerPin> Pins { get; } = [MacArm64, WindowsX64, LinuxX64];

    /// <summary>The pin for the machine running rig; anything else is an error that says what is supported.</summary>
    public static VtracerPin PinForThisMachine()
    {
        var architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
        {
            return MacArm64;
        }

        if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
        {
            return WindowsX64;
        }

        if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
        {
            return LinuxX64;
        }

        throw new PlatformNotSupportedException(
            $"expected macOS arm64, Windows x64 or Linux x64 to run the pinned VTracer {Version}, found {RuntimeInformation.OSDescription} {architecture}");
    }

    /// <summary>Checks <paramref name="bytes"/> against a pinned digest; <paramref name="what"/> names them in the error.</summary>
    public static void VerifySha256(ReadOnlySpan<byte> bytes, string expected, string what)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new VtracerChecksumException($"expected {what} to have SHA-256 {expected}, found {actual}");
        }
    }

    /// <summary>Drops VTracer's XML prolog and gives its root a viewBox so it scales when rendered.</summary>
    public static string WithViewBox(string svg)
    {
        var opening = SvgOpen().Match(svg);
        if (!opening.Success)
        {
            throw new InvalidDataException("VTracer output has no <svg> element");
        }

        var tag = opening.Value;
        var body = svg[opening.Index..];
        var width = Width().Match(tag);
        var height = Height().Match(tag);
        if (tag.Contains("viewBox=", StringComparison.Ordinal) || !width.Success || !height.Success)
        {
            return body;
        }

        return string.Concat(tag.AsSpan(0, tag.Length - 1), $" viewBox=\"0 0 {width.Groups[1].Value} {height.Groups[1].Value}\">", body.AsSpan(tag.Length));
    }

    [GeneratedRegex("<svg\\b[^>]*>")]
    private static partial Regex SvgOpen();

    [GeneratedRegex("\\bwidth=\"([0-9.]+)\"")]
    private static partial Regex Width();

    [GeneratedRegex("\\bheight=\"([0-9.]+)\"")]
    private static partial Regex Height();
}
