using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Estate;

/// <summary>
/// <see cref="InstalledApps"/> over bundles in a temp folder and Uninstall entries built in memory. Ported from the apps
/// cases of <c>tests/test_estate_system.py</c> and <c>tests/test_estate_windows.py</c>.
/// </summary>
public sealed class InstalledAppsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private InstalledApps Apps => new(_proc, new Walked(), new ManualClock(Now));

    private string Bundle(string relative, int bytes)
    {
        var contents = _root.Folder(Path.Join(relative, "Contents"));
        File.WriteAllText(Path.Join(contents, "Info.plist"), "dummy plist");
        File.WriteAllBytes(Path.Join(contents, "binary"), new byte[bytes]);
        return Path.GetDirectoryName(contents)!;
    }

    [Fact]
    public async Task A_bundle_is_found_with_its_size_and_the_version_defaults_reads()
    {
        var bundle = Bundle("Applications/FakeApp.app", 2000);
        _ = _proc.Answer(new Ran(0, "1.2.3\n", string.Empty), "defaults", "read");
        _ = _root.Folder("Applications/NotAnApp");

        var found = await Apps.ScanBundlesAsync([Path.Join(_root.Path, "Applications"), Path.Join(_root.Path, "absent")], 100, Token);

        var app = Assert.Single(found);
        Assert.Equal(("FakeApp.app", bundle, "1.2.3"), (app.Name, app.Path, app.Version));
        Assert.Equal(2000 + "dummy plist".Length, app.Bytes);
        Assert.Equal(["defaults", "read", Path.Join(bundle, "Contents", "Info.plist"), "CFBundleShortVersionString"], _proc.Requests.Single().Argv);
    }

    [Fact]
    public async Task A_version_defaults_cannot_read_is_unknown()
    {
        _ = Bundle("Applications/Quiet.app", 10);

        var app = Assert.Single(await Apps.ScanBundlesAsync([Path.Join(_root.Path, "Applications")], 1, Token));

        Assert.Equal("unknown", app.Version);
    }

    [Fact]
    public async Task The_same_name_in_two_folders_is_listed_twice_and_each_is_held_to_the_floor()
    {
        _ = Bundle("sys/Foo.app", 100);
        var user = Bundle("user/Foo.app", 200_000);
        string[] dirs = [Path.Join(_root.Path, "sys"), Path.Join(_root.Path, "user"), Path.Join(_root.Path, "user")];

        Assert.Equal([user], (await Apps.ScanBundlesAsync(dirs, 100_000, Token)).Select(app => app.Path));
        Assert.Equal(2, (await Apps.ScanBundlesAsync(dirs, 1, Token)).Count);
    }

    [Theory]
    [InlineData(200, 200, true)]
    [InlineData(10, 10, false)]
    [InlineData(400, 5, false)]
    public void An_app_is_dormant_when_neither_written_nor_read_for_180_days(int modifiedDaysAgo, int accessedDaysAgo, bool dormant)
    {
        var app = new InstalledApp("X.app", _root.Path, 1, "1", Now.AddDays(-modifiedDaysAgo), Now.AddDays(-accessedDaysAgo));

        Assert.Equal(dormant, app.IsDormant(Now));
    }

    [Fact]
    public async Task An_uninstall_entry_uses_its_estimated_size_and_install_date()
    {
        var app = await Apps.EntryToAppAsync(
            new UninstallEntry(DisplayName: "Docker Desktop", DisplayVersion: "4.40", EstimatedSize: 2048, InstallDate: "20240101"), measure: false, Token);

        Assert.NotNull(app);
        Assert.Equal((2048L * 1024, "4.40", "."), (app.Bytes, app.Version, app.Path));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), app.Modified);
        Assert.True(app.IsDormant(Now));
    }

    [Fact]
    public async Task An_uninstall_entry_with_no_size_measures_its_quoted_install_folder()
    {
        var folder = _root.Folder("Programs/Tool");
        await File.WriteAllBytesAsync(Path.Join(folder, "tool.exe"), new byte[300], Token);

        var app = await Apps.EntryToAppAsync(new UninstallEntry(DisplayName: "Tool", InstallLocation: $" \"{folder}\" "), measure: true, Token);

        Assert.NotNull(app);
        Assert.Equal((300L, folder, "unknown"), (app.Bytes, app.Path, app.Version));
    }

    public static TheoryData<UninstallEntry> NotApps => new(
        new UninstallEntry(),
        new UninstallEntry(DisplayName: "  "),
        new UninstallEntry(DisplayName: "KB123", SystemComponent: 1),
        new UninstallEntry(DisplayName: "Upd", ParentKeyName: "X"));

    [Theory]
    [MemberData(nameof(NotApps))]
    public async Task An_entry_that_is_not_an_app_is_skipped(UninstallEntry entry) =>
        Assert.Null(await Apps.EntryToAppAsync(entry, measure: false, Token));

    [Fact]
    public async Task The_registry_lists_a_product_registered_in_both_hives_once_largest_first()
    {
        var registry = new Listed(
            new UninstallEntry(DisplayName: "Small", DisplayVersion: "1", EstimatedSize: 10),
            new UninstallEntry(DisplayName: "Big", DisplayVersion: "2", EstimatedSize: 500),
            new UninstallEntry(DisplayName: "big", DisplayVersion: "2", EstimatedSize: 500),
            new UninstallEntry(DisplayName: "Tiny", DisplayVersion: "1", EstimatedSize: 1));

        var found = await Apps.ScanRegistryAsync(registry, 5 * 1024, Token);

        Assert.Equal(["Big", "Small"], found.Select(app => app.Name));
    }

    [Fact]
    public void A_file_time_rounds_to_the_microsecond_as_python_does()
    {
        var stamp = DateTime.UnixEpoch.AddTicks((1_700_000_000L * TimeSpan.TicksPerSecond) + 5_657_755);

        var value = PythonTime.FromStat(stamp);

        Assert.Equal("2023-11-14T22:13:20.565776+00:00", PythonTime.IsoFormat(value));
        Assert.Equal("2023-11-14T22:13:20+00:00", PythonTime.IsoFormat(PythonTime.FromStat(DateTime.UnixEpoch.AddSeconds(1_700_000_000))));
    }

    private sealed class Listed(params UninstallEntry[] entries) : IUninstallRegistry
    {
        public IReadOnlyList<UninstallEntry> Entries() => entries;
    }
}
