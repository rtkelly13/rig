using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Estate;
using Rig.Core.Estate;
using Rig.Core.Tests;
using Rig.Core.Tests.Estate;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Cli.Tests.Estate;

/// <summary>
/// <c>rig estate apps</c> over Uninstall entries built in memory and bundles in a temp folder: no real application is
/// read. Ported from the apps cases of <c>tests/test_estate_cli.py</c>.
/// </summary>
public sealed class AppsCommandTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static UninstallEntry[] Machine =>
    [
        new(DisplayName: "New", DisplayVersion: "2.0", EstimatedSize: GB / 1024, InstallDate: "20261101"),
        new(DisplayName: "Old", DisplayVersion: "1.0", EstimatedSize: 3 * GB / 1024, InstallDate: "20200101"),
    ];

    private async Task<(int Code, string Out)> RunAsync(IUninstallRegistry? registry, params string[] args)
    {
        using var output = new StringWriter();
        var clock = new ManualClock(Now);
        var command = new AppsCommand(new InstalledApps(new FakeProc(), new Walked(), clock), [Path.Join(_root.Path, "Applications")], registry, clock, output);
        var code = await new RootCommand { command.Build() }.Parse(["apps", .. args]).InvokeAsync(new InvocationConfiguration { Output = output }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_json_marks_the_dormant_ones_largest_first()
    {
        var (code, output) = await RunAsync(new Listed(Machine), "--json");

        Assert.Equal(0, code);
        var payload = Assert.IsType<JsonArray>(JsonNode.Parse(output));
        Assert.Equal(
            [("Old", "1.0", true, "2020-01-01T00:00:00+00:00"), ("New", "2.0", false, "2026-11-01T00:00:00+00:00")],
            payload.Select(app => ((string)app!["name"]!, (string)app["version"]!, (bool)app["is_dormant"]!, (string)app["modified"]!)));
        Assert.Equal(["name", "path", "bytes", "version", "modified", "accessed", "is_dormant"], payload[0]!.AsObject().Select(pair => pair.Key));
    }

    [Fact]
    public async Task The_table_totals_the_dormant_space()
    {
        var (code, output) = await RunAsync(new Listed(Machine));

        Assert.Equal(0, code);
        var lines = output.Split('\n');
        Assert.Equal("Installed Applications", lines[0]);
        Assert.Matches(@"^Application\s+Size\s+Version\s+Last Modified\s+Status$", lines[1]);
        Assert.Matches(@"^Old\s+3\.0GB\s+1\.0\s+2020-01-01\s+dormant$", lines[2]);
        Assert.Matches(@"^New\s+1\.0GB\s+2\.0\s+2026-11-01\s+active$", lines[3]);
        Assert.Equal(["", "4.0GB across 2 applications · 3.0GB in dormant apps (>180 days untouched)", ""], lines[4..]);
    }

    [Fact]
    public async Task Without_the_registry_it_walks_the_bundles_above_the_floor()
    {
        var contents = _root.Folder("Applications/Big.app/Contents");
        await File.WriteAllBytesAsync(Path.Join(contents, "binary"), new byte[2048], Token);
        _ = _root.File("Applications/Small.app/Contents/binary");

        var (code, output) = await RunAsync(null, "--min", "1KB", "--json");

        Assert.Equal(0, code);
        var app = Assert.Single(Assert.IsType<JsonArray>(JsonNode.Parse(output)))!;
        Assert.Equal(("Big.app", 2048L, "unknown"), ((string)app["name"]!, (long)app["bytes"]!, (string)app["version"]!));
    }

    private sealed class Listed(params UninstallEntry[] entries) : IUninstallRegistry
    {
        public IReadOnlyList<UninstallEntry> Entries() => entries;
    }
}
