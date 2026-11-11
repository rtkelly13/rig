using System.Runtime.InteropServices;

namespace Rig.Core.Otel;

/// <summary>One release archive of the core collector, its pinned sha256, and the binary inside it.</summary>
/// <param name="Asset">The archive's name on the release.</param>
/// <param name="Sha256">The archive's pinned sha256, from the release's asset digest.</param>
/// <param name="Binary">The binary at the archive's top level.</param>
public sealed record Release(string Asset, string Sha256, string Binary);

/// <summary>One downloadable on-demand artifact.</summary>
/// <param name="Url">Where it is downloaded from.</param>
/// <param name="Sha256">Its pinned sha256.</param>
/// <param name="Binary">The installed binary's name.</param>
/// <param name="Member">The binary's path inside a gzipped tarball; empty for a bare binary.</param>
public sealed record Pin(Uri Url, string Sha256, string Binary, string Member = "");

/// <summary>A platform with no pinned build.</summary>
public sealed class UnsupportedPlatformException : Exception
{
    /// <summary>Creates the exception.</summary>
    public UnsupportedPlatformException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public UnsupportedPlatformException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and its cause.</summary>
    public UnsupportedPlatformException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The pinned binaries, ported from <c>rig.otel_stack.collector</c> and <c>rig.otel_stack.pinned</c>. Every archive's
/// sha256 is pinned from the GitHub release's asset digest and checked before anything is unpacked.
/// </summary>
/// <remarks>
/// <para>The always-on collector is the official core distribution (<c>otelcol</c>), not contrib: measured at 22 MB
/// resident after start and 3 MB after two idle minutes, against contrib's 131 MB and 37 MB for the same work.</para>
/// <para>On demand: ClickHouse <see cref="ClickHouseVersion"/>, which has no native Windows build (so Windows has no pin
/// and <c>up</c> says so instead of half-starting), and <c>otelcol-contrib</c> at the core collector's version, for the
/// <c>clickhouse</c> exporter.</para>
/// </remarks>
public static class OtelPins
{
    /// <summary>The core and contrib collector release.</summary>
    public const string Version = "0.162.0";

    /// <summary>The ClickHouse release.</summary>
    public const string ClickHouseVersion = "26.8.23.6-lts";

    /// <summary>Where the core collector's releases are downloaded from.</summary>
    public const string ReleasesUrl = "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download";

    private const string ClickHouseUrl = $"https://github.com/ClickHouse/ClickHouse/releases/download/v{ClickHouseVersion}";
    private const string ContribUrl = $"{ReleasesUrl}/v{Version}";

    /// <summary>The core collector, by platform key.</summary>
    public static IReadOnlyDictionary<string, Release> Releases { get; } = new Dictionary<string, Release>(StringComparer.Ordinal)
    {
        ["darwin_arm64"] = new("otelcol_0.162.0_darwin_arm64.tar.gz", "53de34c02913f54aa240861e0febf67a6839bc8ecf1bfab83a8ae7d355d30674", "otelcol"),
        ["darwin_amd64"] = new("otelcol_0.162.0_darwin_amd64.tar.gz", "7292fdb7673283b5fa4422c33703b846011426f79ac7b0505ed9c314a256870d", "otelcol"),
        ["linux_amd64"] = new("otelcol_0.162.0_linux_amd64.tar.gz", "f99929987a915d3c6b2c9b15bc4938c5cea903a37a3f49e478024a0fa0772339", "otelcol"),
        ["windows_amd64"] = new("otelcol_0.162.0_windows_amd64.tar.gz", "480381f40e182e638ac49def6c28ef6c60bcb8b342056dd9d0b9b906146b8f82", "otelcol.exe"),
    };

    /// <summary>ClickHouse, by platform key.</summary>
    public static IReadOnlyDictionary<string, Pin> ClickHouse { get; } = new Dictionary<string, Pin>(StringComparer.Ordinal)
    {
        ["darwin_arm64"] = new(new($"{ClickHouseUrl}/clickhouse-macos-aarch64"), "ffd86ef015a85753916d6cffeb202c531109b5f35ef485a2ec87805aff5cb66a", "clickhouse"),
        ["darwin_amd64"] = new(new($"{ClickHouseUrl}/clickhouse-macos"), "069fb0f152bbfd1934b9f5194e8d503459b343637b86c3ed9996902d13926f18", "clickhouse"),
        ["linux_amd64"] = new(new($"{ClickHouseUrl}/clickhouse-common-static-26.8.23.6-amd64.tgz"), "14e50330a5b3485e18bb0c6b81637fc1a91567a8eb4ee43062cee5dc552d593b", "clickhouse", "clickhouse-common-static-26.8.23.6/usr/bin/clickhouse"),
    };

    /// <summary>The contrib collector, by platform key.</summary>
    public static IReadOnlyDictionary<string, Pin> Contrib { get; } = new Dictionary<string, Pin>(StringComparer.Ordinal)
    {
        ["darwin_arm64"] = new(new($"{ContribUrl}/otelcol-contrib_0.162.0_darwin_arm64.tar.gz"), "d5e11974d2e4adac3cc001a25137aad52f6e77ec3034ba7735cac7c219a5f97a", "otelcol-contrib", "otelcol-contrib"),
        ["darwin_amd64"] = new(new($"{ContribUrl}/otelcol-contrib_0.162.0_darwin_amd64.tar.gz"), "91a6e7a0f5e1981986c897db4535de5ba8d7fc45d4205809aecb9ebebf68c71c", "otelcol-contrib", "otelcol-contrib"),
        ["linux_amd64"] = new(new($"{ContribUrl}/otelcol-contrib_0.162.0_linux_amd64.tar.gz"), "fcc063749f730f8c21fe29f2d340ff174f5f1c5885bd3156fb6c985a3036fcc3", "otelcol-contrib", "otelcol-contrib"),
        ["windows_amd64"] = new(new($"{ContribUrl}/otelcol-contrib_0.162.0_windows_amd64.tar.gz"), "b371829181e89947a1a6570b8b6c38c20d85e2d215b76bf042f89b365b9e5c3d", "otelcol-contrib.exe", "otelcol-contrib.exe"),
    };

    /// <summary>This machine's platform key, such as <c>darwin_arm64</c>.</summary>
    /// <exception cref="UnsupportedPlatformException">No core collector is pinned for it.</exception>
    public static string PlatformKey() => PlatformKey(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture);

    /// <summary>The key for <paramref name="os"/> on <paramref name="architecture"/>.</summary>
    /// <exception cref="UnsupportedPlatformException">No core collector is pinned for it.</exception>
    public static string PlatformKey(string os, Architecture architecture)
    {
        var arch = architecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "amd64",
            _ => architecture.ToString().ToLowerInvariant(),
        };
        var key = $"{os}_{arch}";
        return Releases.ContainsKey(key) ? key : throw new UnsupportedPlatformException($"no pinned otelcol {Version} build for {key}");
    }

    /// <summary>The ClickHouse pin for <paramref name="key"/>.</summary>
    /// <exception cref="UnsupportedPlatformException">There is none (Windows).</exception>
    public static Pin ClickHousePin(string key) =>
        ClickHouse.GetValueOrDefault(key)
        ?? throw new UnsupportedPlatformException($"ClickHouse has no native build for {key}; on Windows run it under WSL or Docker");

    /// <summary>The contrib collector pin for <paramref name="key"/>.</summary>
    /// <exception cref="UnsupportedPlatformException">There is none.</exception>
    public static Pin ContribPin(string key) =>
        Contrib.GetValueOrDefault(key) ?? throw new UnsupportedPlatformException($"no pinned otelcol-contrib {Version} build for {key}");

    /// <summary>Where the core collector is installed under <paramref name="home"/> (the otel folder).</summary>
    public static string InstallDir(string home) => Path.Join(home, "bin", Version);

    /// <summary>The core collector's binary for <paramref name="key"/> under <paramref name="home"/>.</summary>
    public static string BinaryPath(string home, string key) => Path.Join(InstallDir(home), Releases[key].Binary);

    /// <summary>One folder per pinned digest, so a new pin never mixes with an old binary.</summary>
    public static string PinDir(Pin pin, string home)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return Path.Join(home, "bin", pin.Sha256[..16]);
    }

    /// <summary>The pinned binary under <paramref name="home"/>.</summary>
    public static string PinBinary(Pin pin, string home) => Path.Join(PinDir(pin, home), pin.Binary);
}
