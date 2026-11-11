using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>One installed application: what it occupies, its version, and when it was last touched.</summary>
/// <param name="Name">The bundle name on macOS (<c>Foo.app</c>), the display name on Windows.</param>
/// <param name="Path">The bundle, or the install folder; <c>.</c> when Windows names none.</param>
/// <param name="Bytes">What it occupies on disk.</param>
/// <param name="Version">Its version, or <c>unknown</c>.</param>
/// <param name="Modified">When it last changed.</param>
/// <param name="Accessed">When it was last opened; Windows records no such thing, so there it is <paramref name="Modified"/>.</param>
public sealed record InstalledApp(string Name, string Path, long Bytes, string Version, DateTimeOffset Modified, DateTimeOffset Accessed)
{
    /// <summary>Untouched for more than this many whole days makes an app dormant.</summary>
    public const int DormantDays = 180;

    /// <summary>Whether it has been neither modified nor opened in more than <see cref="DormantDays"/> days.</summary>
    public bool IsDormant(DateTimeOffset now) => (now - (Modified > Accessed ? Modified : Accessed)).Days > DormantDays;
}

/// <summary>
/// The values of one Uninstall key the Windows scan reads; each is whatever the installer wrote (a string, a number),
/// or null when it wrote nothing.
/// </summary>
public sealed record UninstallEntry(
    object? DisplayName = null,
    object? DisplayVersion = null,
    object? EstimatedSize = null,
    object? InstallDate = null,
    object? InstallLocation = null,
    object? SystemComponent = null,
    object? ParentKeyName = null);

/// <summary>The Uninstall keys Settings &gt; Apps lists. A seam, so tests read entries without a registry.</summary>
public interface IUninstallRegistry
{
    /// <summary>Every entry: machine-wide 64-bit, then machine-wide 32-bit (WOW6432Node), then per-user.</summary>
    IReadOnlyList<UninstallEntry> Entries();
}

/// <summary>The real Uninstall keys. A hive or key that cannot be opened contributes nothing.</summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Windows registry only; InstalledApps is tested through IUninstallRegistry.")]
public sealed class UninstallRegistry : IUninstallRegistry
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <inheritdoc />
    public IReadOnlyList<UninstallEntry> Entries() =>
        [.. Hive(Registry.LocalMachine, Uninstall), .. Hive(Registry.LocalMachine, UninstallWow), .. Hive(Registry.CurrentUser, Uninstall)];

    private static List<UninstallEntry> Hive(RegistryKey root, string subkey)
    {
        var entries = new List<UninstallEntry>();
        try
        {
            using var parent = root.OpenSubKey(subkey);
            foreach (var name in parent?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var child = parent!.OpenSubKey(name);
                    if (child is not null)
                    {
                        entries.Add(new UninstallEntry(
                            Value(child, "DisplayName"),
                            Value(child, "DisplayVersion"),
                            Value(child, "EstimatedSize"),
                            Value(child, "InstallDate"),
                            Value(child, "InstallLocation"),
                            Value(child, "SystemComponent"),
                            Value(child, "ParentKeyName")));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // An entry that cannot be read is skipped, as the rest of the hive is still worth listing.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return entries;
        }

        return entries;
    }

    // A DWORD reads back unsigned, as winreg gives it; an expandable string as written.
    private static object? Value(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is var value && value is int dword ? (long)(uint)dword : value;
}

/// <summary>
/// Installed applications by size, version and when they were last touched, so the dormant ones stand out. Ported from
/// <c>src/rig/estate/apps.py</c> and <c>apps_windows.py</c>: macOS walks the <c>.app</c> bundles; Windows has no such
/// folder, so it reads the Uninstall keys, measuring the install folder when the installer left no size.
/// </summary>
/// <param name="proc">Runs <c>defaults</c> for a bundle's version.</param>
/// <param name="disk">Sizes a bundle or install folder.</param>
/// <param name="time">What "now" is when nothing records when an app changed.</param>
public sealed class InstalledApps(IProc proc, IDiskUsage disk, TimeProvider time)
{
    /// <summary>How long <c>defaults</c> may take to read one bundle's version.</summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The folders a Mac keeps applications in.</summary>
    public static IReadOnlyList<string> DefaultAppDirs(string home) => ["/Applications", System.IO.Path.Join(home, "Applications")];

    /// <summary>
    /// Every <c>.app</c> bundle in <paramref name="appDirs"/> at or above <paramref name="minSize"/>, largest first. A
    /// bundle reached twice (a link, a repeated folder) counts once; two installs sharing a name are both listed.
    /// </summary>
    public async Task<IReadOnlyList<InstalledApp>> ScanBundlesAsync(IEnumerable<string> appDirs, long minSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appDirs);
        var apps = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var appDir in appDirs)
        {
            foreach (var bundle in Bundles(appDir))
            {
                if (!seen.Add(PathResolution.Resolve(bundle)))
                {
                    continue;
                }

                var size = await disk.SizeAsync(bundle, cancellationToken).ConfigureAwait(false);
                if (size < minSize)
                {
                    continue;
                }

                var (modified, accessed) = Directory.Exists(bundle)
                    ? (PythonTime.FromStat(Directory.GetLastWriteTimeUtc(bundle)), PythonTime.FromStat(Directory.GetLastAccessTimeUtc(bundle)))
                    : (time.GetUtcNow(), time.GetUtcNow());
                var version = await BundleVersionAsync(bundle, cancellationToken).ConfigureAwait(false);
                apps.Add(new InstalledApp(System.IO.Path.GetFileName(bundle), bundle, size, version, modified, accessed));
            }
        }

        return [.. apps.OrderByDescending(app => app.Bytes)];
    }

    /// <summary>
    /// Every app <paramref name="registry"/> lists at or above <paramref name="minSize"/>, largest first. The same product
    /// often registers in both the 32- and 64-bit hives, so a name and version is listed once.
    /// </summary>
    public async Task<IReadOnlyList<InstalledApp>> ScanRegistryAsync(IUninstallRegistry registry, long minSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var apps = new List<InstalledApp>();
        var seen = new HashSet<(string, string)>();
        foreach (var entry in registry.Entries())
        {
            var app = await EntryToAppAsync(entry, measure: true, cancellationToken).ConfigureAwait(false);
            if (app is null || app.Bytes < minSize || !seen.Add((app.Name.ToLowerInvariant(), app.Version)))
            {
                continue;
            }

            apps.Add(app);
        }

        return [.. apps.OrderByDescending(app => app.Bytes)];
    }

    /// <summary>
    /// The app one Uninstall key describes, or null for an entry that is not an app: no name, a system component, or an
    /// update parented to another entry. With no <c>EstimatedSize</c>, <paramref name="measure"/> sizes the install folder.
    /// </summary>
    public async Task<InstalledApp?> EntryToAppAsync(UninstallEntry entry, bool measure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.DisplayName is not string name || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Updates and runtime components are children of an app, not apps of their own.
        if (Number(entry.SystemComponent) == 1 || Truthy(entry.ParentKeyName))
        {
            return null;
        }

        var location = entry.InstallLocation is string raw && !string.IsNullOrWhiteSpace(raw) ? raw.Trim().Trim('"') : null;
        var size = Number(entry.EstimatedSize) is > 0 and var kib ? kib * 1024 : 0;
        if (size == 0 && measure && location is not null && Directory.Exists(location))
        {
            size = await disk.SizeAsync(location, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset? installed = entry.InstallDate is string date
            && DateTime.TryParseExact(date.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day)
            ? new DateTimeOffset(day, TimeSpan.Zero)
            : null;
        DateTimeOffset? touched = location is not null && Path.Exists(location) ? PythonTime.FromStat(File.GetLastWriteTimeUtc(location)) : null;
        var modified = (installed, touched) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => time.GetUtcNow(),
        };

        var version = entry.DisplayVersion is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : "unknown";
        return new InstalledApp(name.Trim(), location ?? ".", size, version, modified, modified);
    }

    private static List<string> Bundles(string appDir)
    {
        try
        {
            return [.. Directory.EnumerateFileSystemEntries(appDir).Where(entry => Suffix(entry) == ".app" && Directory.Exists(entry))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<string> BundleVersionAsync(string bundle, CancellationToken cancellationToken)
    {
        var plist = System.IO.Path.Join(bundle, "Contents", "Info.plist");
        if (!File.Exists(plist))
        {
            return "unknown";
        }

        var ran = await proc.RunAsync(new ProcRequest(["defaults", "read", plist, "CFBundleShortVersionString"]) { Timeout = VersionTimeout }, cancellationToken).ConfigureAwait(false);
        return ran.OutOrEmpty is { Length: > 0 } version ? version : "unknown";
    }

    /// <summary>A name's extension as Python's <c>Path.suffix</c> reads it: none for a dot-file such as <c>.app</c>.</summary>
    internal static string Suffix(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[dot..];
    }

    private static long? Number(object? value) => value switch
    {
        int i => i,
        long l => l,
        uint u => u,
        ulong u when u <= long.MaxValue => (long)u,
        _ => null,
    };

    // Python's truthiness, for a value the installer may have written as a string or a number.
    private static bool Truthy(object? value) => value switch
    {
        null => false,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => Number(value) is not 0,
    };
}

/// <summary>Timestamps as Python's <c>datetime</c> makes and prints them, so a ported command's JSON is unchanged.</summary>
public static class PythonTime
{
    /// <summary>
    /// A file time as <c>datetime.fromtimestamp(st.st_mtime, tz=UTC)</c> gives it: through the same float seconds, rounded
    /// half-to-even to the microsecond. .NET keeps a file time to 100 ns, dropping the last two digits of the nanoseconds
    /// Python reads, so a fraction is taken at the middle of the 100 ns it lies in: that decides the rounding the way the
    /// full value would, except in the rare case it lies within 50 ns of half a microsecond.
    /// </summary>
    public static DateTimeOffset FromStat(DateTime utc)
    {
        var since = DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch;
        var seconds = Math.DivRem(since.Ticks, TimeSpan.TicksPerSecond, out var rest);
        var stamp = seconds + (rest == 0 ? 0 : ((rest * 100) + 50) * 1e-9);
        var whole = Math.Floor(stamp);
        var micros = (long)Math.Round((stamp - whole) * 1e6, MidpointRounding.ToEven);
        return DateTimeOffset.UnixEpoch.AddTicks((((long)whole * 1_000_000) + micros) * 10);
    }

    /// <summary><c>datetime.isoformat()</c> of a UTC time: microseconds only when there are any, and a <c>+00:00</c> offset.</summary>
    public static string IsoFormat(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var format = utc.Ticks / 10 % 1_000_000 == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.ffffff";
        return utc.ToString(format, CultureInfo.InvariantCulture) + "+00:00";
    }
}
