using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Rig.Cli.Image;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Image.Pathways;

namespace Rig.Cli.Tests.Image;

/// <summary>The pinned VTracer process: verified before install, verified before every run, and its output given a viewBox.</summary>
public sealed class VtracerToolTests : IDisposable
{
    private static readonly byte[] Binary = "#!/bin/sh\nfake vtracer\n"u8.ToArray();
    private const string Prolog = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- Generator: visioncortex VTracer 0.6.4 -->\n";
    private const string Traced = Prolog + "<svg version=\"1.1\" xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"20\">\n<path d=\"M0 0 Z\"/>\n</svg>\n";

    private readonly TempDir _temp = new();
    private readonly List<Uri> _downloads = [];

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task The_archive_is_checked_then_the_binary_runs_and_the_output_gains_a_viewBox()
    {
        var archive = TarGz(Binary, "vtracer");
        var proc = ProcWritingTrace();
        var tool = Tool(proc, Pin("vtracer.tar.gz", archive, Binary), archive);

        var svg = await tool.TraceAsync("ref.png", new VtracerConfig(), TestContext.Current.CancellationToken);

        Assert.StartsWith("<svg version=\"1.1\"", svg, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 10 20\"", svg, StringComparison.Ordinal);
        var request = Assert.Single(proc.Requests);
        Assert.EndsWith("vtracer", request.Argv[0], StringComparison.Ordinal);
        Assert.Equal("--input", request.Argv[1]);
        Assert.Equal(Path.GetFullPath("ref.png"), request.Argv[2]);
        Assert.Single(_downloads);
    }

    [Fact]
    public async Task A_second_trace_reuses_the_installed_binary()
    {
        var archive = TarGz(Binary, "vtracer");
        var tool = Tool(ProcWritingTrace(), Pin("vtracer.tar.gz", archive, Binary), archive);

        await tool.TraceAsync("ref.png", new VtracerConfig(), TestContext.Current.CancellationToken);
        await tool.TraceAsync("ref.png", new VtracerConfig(), TestContext.Current.CancellationToken);

        Assert.Single(_downloads);
    }

    [Fact]
    public async Task A_zip_archive_is_unpacked_too()
    {
        var archive = Zip(Binary, "vtracer.exe");
        var tool = Tool(ProcWritingTrace(), Pin("vtracer.zip", archive, Binary, "vtracer.exe"), archive);

        var svg = await tool.TraceAsync("ref.png", new VtracerConfig(), TestContext.Current.CancellationToken);

        Assert.Contains("viewBox=", svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_archive_is_refused_before_anything_is_installed()
    {
        var good = TarGz(Binary, "vtracer");
        var proc = ProcWritingTrace();
        var tool = Tool(proc, Pin("vtracer.tar.gz", good, Binary), TarGz("#!/bin/sh\nevil\n"u8.ToArray(), "vtracer"));

        var exception = await Assert.ThrowsAsync<VtracerChecksumException>(() => tool.TraceAsync("ref.png", new VtracerConfig(), TestContext.Current.CancellationToken));

        Assert.Contains("the archive from", exception.Message, StringComparison.Ordinal);
        Assert.Empty(proc.Requests);
        Assert.Empty(Directory.GetFiles(_temp.Path, "vtracer", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_binary_that_does_not_match_its_pin_is_refused()
    {
        var archive = TarGz(Binary, "vtracer");
        var proc = ProcWritingTrace();
        var tool = Tool(proc, Pin("vtracer.tar.gz", archive, "other"u8.ToArray()), archive);

        var exception = await Assert.ThrowsAsync<VtracerChecksumException>(() => tool.TraceAsync("ref.png", new VtracerConfig(), TestContext.Current.CancellationToken));

        Assert.Contains("vtracer from", exception.Message, StringComparison.Ordinal);
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task An_installed_binary_changed_after_install_is_not_run()
    {
        var archive = TarGz(Binary, "vtracer");
        var proc = ProcWritingTrace();
        var tool = Tool(proc, Pin("vtracer.tar.gz", archive, Binary), archive);
        await tool.TraceAsync("ref.png", new VtracerConfig(), TestContext.Current.CancellationToken);
        var installed = Directory.GetFiles(_temp.Path, "vtracer", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(installed, "tampered", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<VtracerChecksumException>(() => tool.TraceAsync("ref.png", new VtracerConfig(), TestContext.Current.CancellationToken));

        Assert.Contains("the installed", exception.Message, StringComparison.Ordinal);
        Assert.Single(proc.Requests);
    }

    [Fact]
    public async Task A_failing_vtracer_reports_its_exit_code_and_stderr()
    {
        var archive = TarGz(Binary, "vtracer");
        var proc = new FakeProc { Unanswered = new Ran(3, string.Empty, "bad image") };
        var tool = Tool(proc, Pin("vtracer.tar.gz", archive, Binary), archive);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.TraceAsync("ref.png", new VtracerConfig(), TestContext.Current.CancellationToken));

        Assert.Contains("found 3: bad image", exception.Message, StringComparison.Ordinal);
    }

    private static FakeProc ProcWritingTrace() => new()
    {
        Unanswered = new Ran(0, string.Empty, string.Empty),
        BeforeRun = request => File.WriteAllText(request.Argv[request.Argv.ToList().IndexOf("--output") + 1], Traced),
    };

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static VtracerPin Pin(string archiveName, byte[] archive, byte[] binary, string entry = "vtracer") =>
        new("test", new Uri($"https://example.invalid/{archiveName}"), Sha(archive), entry, Sha(binary));

    private VtracerTool Tool(FakeProc proc, VtracerPin pin, byte[] served) =>
        new(proc, _temp.Path, (url, _) =>
        {
            _downloads.Add(url);
            return Task.FromResult(served);
        }, pin);

    private static byte[] TarGz(byte[] content, string name)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./" + name) { DataStream = new MemoryStream(content) });
        }

        return buffer.ToArray();
    }

    private static byte[] Zip(byte[] content, string name)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(content);
        }

        return buffer.ToArray();
    }
}
