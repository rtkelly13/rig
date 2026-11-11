using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Otel;

/// <summary>
/// The collector's installer, detached start and runtime. Nothing here touches the network, the live collector's
/// ports, or a process it did not start: downloads are answered by a stub handler, process queries by
/// <see cref="FakeProc"/>, and the real spawns start <c>sleep</c>, <c>echo</c> or <c>ping</c> in a temp folder.
/// </summary>
public sealed class OtelRuntimeTests : IDisposable
{
    private const string Binary = "/opt/otel/bin/otelcol";
    private const string Started = "Wed Nov 11 09:00:00 2026";

    private readonly TempDir temp = new();
    private readonly FakeProc proc = new();
    private readonly FakeDetached detached = new();
    private readonly List<Uri> fetched = [];
    private readonly Handler handler;
    private readonly HttpClient http;
    private byte[] body = [];
    private HttpStatusCode status = HttpStatusCode.OK;

    public OtelRuntimeTests()
    {
        handler = new Handler(request =>
        {
            fetched.Add(request.RequestUri!);
            return new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        });
        http = new HttpClient(handler, disposeHandler: false);
    }

    public void Dispose()
    {
        http.Dispose();
        handler.Dispose();
        temp.Dispose();
    }

    [Fact]
    public async Task A_pinned_archive_unpacks_only_its_binary()
    {
        var archive = TarGz(("pkg/tool", "binary"u8.ToArray(), TarEntryType.RegularFile), ("pkg/other", "x"u8.ToArray(), TarEntryType.RegularFile));
        var pin = PinOf(archive, member: "pkg/tool");

        var binary = await Installer(archive).InstallAsync(pin, temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal(OtelPins.PinBinary(pin, temp.Path), binary);
        Assert.Equal("binary", await File.ReadAllTextAsync(binary, TestContext.Current.CancellationToken));
        Assert.Equal(["tool", ".sha256"], Directory.GetFiles(OtelPins.PinDir(pin, temp.Path)).Select(Path.GetFileName).OrderDescending(StringComparer.Ordinal));
        Assert.Equal(pin.Sha256 + "\n", await File.ReadAllTextAsync(Path.Join(OtelPins.PinDir(pin, temp.Path), ".sha256"), TestContext.Current.CancellationToken));
        Assert.True(OtelInstaller.PinVerified(pin, temp.Path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(binary));
        }
    }

    [Fact]
    public async Task A_verified_install_downloads_nothing()
    {
        var archive = TarGz(("tool", "binary"u8.ToArray(), TarEntryType.V7RegularFile));
        var pin = PinOf(archive, member: "tool");
        var installer = Installer(archive);
        _ = await installer.InstallAsync(pin, temp.Path, TestContext.Current.CancellationToken);

        _ = await installer.InstallAsync(pin, temp.Path, TestContext.Current.CancellationToken);

        Assert.Single(fetched);
    }

    [Fact]
    public async Task A_bare_binary_pin_is_moved_into_place()
    {
        var download = "#!/bin/sh\n"u8.ToArray();
        var pin = PinOf(download, member: string.Empty);

        var binary = await Installer(download).InstallAsync(pin, temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal(download, await File.ReadAllBytesAsync(binary, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Join(OtelPins.PinDir(pin, temp.Path), "tool.download")));
    }

    [Fact]
    public async Task A_download_that_is_not_the_pinned_one_is_not_unpacked()
    {
        var archive = TarGz(("tool", "binary"u8.ToArray(), TarEntryType.RegularFile));
        var pin = PinOf(archive, member: "tool") with { Sha256 = new string('0', 64) };

        var error = await Assert.ThrowsAsync<DigestMismatchException>(() => Installer(archive).InstallAsync(pin, temp.Path, TestContext.Current.CancellationToken));

        Assert.Equal($"https://example.test/tool.tar.gz: sha256 {Sha(archive)}, pinned {pin.Sha256}; nothing was unpacked", error.Message);
        Assert.Empty(Directory.GetFiles(OtelPins.PinDir(pin, temp.Path)));
    }

    [Theory]
    [InlineData("pkg/other", TarEntryType.RegularFile)]
    [InlineData("tool", TarEntryType.Directory)]
    [InlineData("tool", TarEntryType.SymbolicLink)]
    public async Task An_archive_without_the_regular_file_is_refused(string name, TarEntryType type)
    {
        var archive = TarGz((name, type is TarEntryType.RegularFile ? "x"u8.ToArray() : [], type));
        var pin = PinOf(archive, member: "tool");

        var error = await Assert.ThrowsAsync<DigestMismatchException>(() => Installer(archive).InstallAsync(pin, temp.Path, TestContext.Current.CancellationToken));

        Assert.Equal("tool.download has no regular file tool", error.Message);
        Assert.Empty(Directory.GetFiles(OtelPins.PinDir(pin, temp.Path)));
    }

    [Fact]
    public async Task The_collector_comes_from_its_release_and_must_match_the_pin()
    {
        var key = OtelPins.PlatformKey("linux", System.Runtime.InteropServices.Architecture.X64);
        var release = OtelPins.Releases[key];
        var junk = "not the release"u8.ToArray();

        var error = await Assert.ThrowsAsync<DigestMismatchException>(() => Installer(junk).InstallCollectorAsync(temp.Path, key, TestContext.Current.CancellationToken));

        Assert.Equal(new Uri($"{OtelPins.ReleasesUrl}/v{OtelPins.Version}/{release.Asset}"), Assert.Single(fetched));
        Assert.Equal($"{release.Asset}: sha256 {Sha(junk)}, pinned {release.Sha256}; nothing was extracted", error.Message);
        Assert.Empty(Directory.GetFiles(OtelPins.InstallDir(temp.Path)));
        Assert.False(OtelInstaller.CollectorVerified(temp.Path, key));
    }

    [Fact]
    public async Task A_verified_collector_is_left_alone()
    {
        const string key = "darwin_arm64";
        var binary = temp.File(Path.Join("bin", OtelPins.Version, "otelcol"));
        await File.WriteAllTextAsync(Path.Join(OtelPins.InstallDir(temp.Path), ".sha256"), OtelPins.Releases[key].Sha256 + "\n", TestContext.Current.CancellationToken);

        Assert.Equal(binary, await Installer([]).InstallCollectorAsync(temp.Path, key, TestContext.Current.CancellationToken));
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task Downloads_are_https_only_and_must_succeed()
    {
        var installer = Installer([], HttpStatusCode.NotFound);
        var target = Path.Join(temp.Path, "f");

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => installer.DownloadAsync(new Uri("http://example.test/f"), target, TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<HttpRequestException>(() => installer.DownloadAsync(new Uri("https://example.test/f"), target, TestContext.Current.CancellationToken));

        Assert.StartsWith("refusing non-HTTPS download: http://example.test/f", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Digest_mismatch_has_the_usual_constructors()
    {
        var inner = new InvalidOperationException("x");

        Assert.NotNull(new DigestMismatchException().Message);
        Assert.Same(inner, new DigestMismatchException("m", inner).InnerException);
    }

    [Fact]
    public void Command_lines_quote_as_list2cmdline_did()
    {
        (string[] Argv, string Line)[] cases =
        [
            (["a", "b c"], "a \"b c\""),
            ([""], "\"\""),
            (["a\\b"], "a\\b"),
            (["a\"b"], "a\\\"b"),
            (["a\\\"b"], "a\\\\\\\"b"),
            (["a b\\"], "\"a b\\\\\""),
            (["a\\\\"], "a\\\\"),
            (["tab\there"], "\"tab\there\""),
            (["C:\\Program Files\\otelcol.exe", "--config", "C:\\x y\\otelcol.yaml"], "\"C:\\Program Files\\otelcol.exe\" --config \"C:\\x y\\otelcol.yaml\""),
        ];

        foreach (var (argv, line) in cases)
        {
            Assert.Equal(line, WindowsCommandLine.Join(argv));
        }
    }

    [Fact]
    public async Task A_unix_detached_start_is_the_program_in_its_own_group()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only; Windows starts through CreateProcessW.");
        var log = Path.Join(temp.Path, "sleep.log");
        var system = new SystemProc();

        var pid = await new UnixDetachedStart(system).StartAsync(["/bin/sleep", "30"], log, temp.Path, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var command = await system.RunAsync(new ProcRequest(["ps", "-o", "command=", "-p", $"{pid}"]), TestContext.Current.CancellationToken);
            var group = await system.RunAsync(new ProcRequest(["ps", "-o", "pgid=", "-p", $"{pid}"]), TestContext.Current.CancellationToken);

            Assert.Contains("sleep 30", command.Out, StringComparison.Ordinal);
            Assert.Equal($"{pid}", group.Out.Trim());
            Assert.True(File.Exists(log));
        }
        finally
        {
            using var started = Process.GetProcessById(pid);
            started.Kill();
        }
    }

    [Fact]
    public async Task A_unix_detached_start_appends_to_its_log()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only; Windows starts through CreateProcessW.");
        var log = Path.Join(temp.Path, "echo.log");
        await File.WriteAllTextAsync(log, "before\n", TestContext.Current.CancellationToken);

        _ = await new UnixDetachedStart(new SystemProc()).StartAsync(["/bin/echo", "a b"], log, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("before\na b\n", await Eventually(log, text => text.EndsWith('\n') && text != "before\n"));
    }

    [Fact]
    public async Task A_unix_detached_start_runs_through_the_shell()
    {
        var program = temp.File("tool");
        var shell = proc.Answer(new Ran(0, "4242\n", string.Empty), "/bin/sh");
        var env = new Dictionary<string, string> { ["K"] = "v" };

        var pid = await new UnixDetachedStart(shell).StartAsync([program, "x"], "l.log", "/w", env, TestContext.Current.CancellationToken);

        var request = Assert.Single(proc.Requests);
        Assert.Equal(4242, pid);
        Assert.Equal(["/bin/sh", "-c", UnixDetachedStart.Script, "sh", "l.log", program, "x"], request.Argv);
        Assert.Equal("/w", request.WorkingDirectory);
        Assert.Same(env, request.Environment);
    }

    [Fact]
    public async Task A_unix_detached_start_fails_loudly()
    {
        var program = temp.File("tool");
        var start = new UnixDetachedStart(proc.Answer(new Ran(0, "no pid", string.Empty), "/bin/sh"));

        _ = await Assert.ThrowsAsync<ProcException>(() => start.StartAsync([program], "l.log", cancellationToken: TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<FileNotFoundException>(() => start.StartAsync([Path.Join(temp.Path, "missing")], "l.log", cancellationToken: TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => start.StartAsync([], "l.log", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task A_windows_detached_start_runs_the_program_and_appends_to_its_log()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var log = Path.Join(temp.Path, "ping.log");
        var ping = Path.Join(Environment.SystemDirectory, "PING.EXE");
        var start = new WindowsDetachedStart();

        var pid = await start.StartAsync([ping, "-n", "30", "127.0.0.1"], log, temp.Path, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            using var started = Process.GetProcessById(pid);
            Assert.Equal("PING", started.ProcessName, ignoreCase: true);
            Assert.Contains("127.0.0.1", await Eventually(log, text => text.Contains("127.0.0.1", StringComparison.Ordinal)), StringComparison.Ordinal);
        }
        finally
        {
            using var started = Process.GetProcessById(pid);
            started.Kill();
        }

        _ = await Assert.ThrowsAsync<NotSupportedException>(() => start.StartAsync([ping], log, environment: new Dictionary<string, string> { ["K"] = "v" }, cancellationToken: TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<FileNotFoundException>(() => start.StartAsync([Path.Join(temp.Path, "missing.exe")], log, cancellationToken: TestContext.Current.CancellationToken));
        _ = await Assert.ThrowsAsync<Win32Exception>(() => start.StartAsync([ping], Path.Join(temp.Path, "no", "such", "log"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_probe_sees_a_listener_and_not_an_empty_port()
    {
        var probe = new TcpPortProbe();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(await probe.ListeningAsync(port, cancellationToken: TestContext.Current.CancellationToken));
        listener.Stop();
        Assert.False(await probe.ListeningAsync(port, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_probe_honours_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TcpPortProbe().ListeningAsync(1, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void Forwarding_is_set_read_and_turned_off()
    {
        var runtime = Runtime();

        Assert.Null(runtime.Forward());
        Assert.Equal("http://127.0.0.1:24318", runtime.SetForward(" http://127.0.0.1:24318/ "));
        Assert.Equal("http://127.0.0.1:24318\n", File.ReadAllText(Paths.Forward));
        Assert.Equal("http://127.0.0.1:24318", runtime.Forward());
        Assert.Throws<FormatException>(() => runtime.SetForward("ftp://x:1"));

        Assert.Null(runtime.SetForward(null));
        Assert.False(File.Exists(Paths.Forward));
        Assert.Null(runtime.SetForward(null));

        File.WriteAllText(Paths.Forward, "  \n");
        Assert.Null(runtime.Forward());
    }

    [Fact]
    public void The_config_carries_the_forward_endpoint()
    {
        var runtime = Runtime();
        _ = runtime.SetForward("http://127.0.0.1:24318");

        var config = runtime.WriteConfig();

        Assert.Equal(CollectorConfig.Render(Paths.Data, "http://127.0.0.1:24318"), File.ReadAllText(config));
        Assert.True(Directory.Exists(Paths.Data));
    }

    [Fact]
    public async Task Start_launches_detached_and_records_the_launch()
    {
        _ = proc.Answer(new Ran(0, Started + "\n", string.Empty), "ps", "-o", "lstart=");

        var pid = await Runtime().StartAsync(Binary, TestContext.Current.CancellationToken);

        Assert.Equal(FakeDetached.Pid, pid);
        Assert.Equal([Binary, "--config", Paths.Config], detached.Argv);
        Assert.Equal(Paths.Log, detached.Log);
        Assert.True(Directory.Exists(Path.GetDirectoryName(Paths.Log)));
        Assert.Equal(new Launched(pid, Binary, Started), Runtime().Recorded());
        Assert.Equal(["ps", "-o", "lstart=", "-p", $"{pid}"], Assert.Single(proc.Requests).Argv);
    }

    [Fact]
    public async Task Windows_records_no_start_time()
    {
        _ = await Runtime(windows: true).StartAsync(Binary, TestContext.Current.CancellationToken);

        Assert.Equal(new Launched(FakeDetached.Pid, Binary), Runtime().Recorded());
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task A_launch_is_ours_while_the_binary_and_start_time_match()
    {
        var runtime = Runtime();
        var record = new Launched(7, Binary, Started);
        _ = proc.Answer(new Ran(0, $"{Binary} --config x\n", string.Empty), "ps", "-o", "command=");
        _ = proc.Answer(new Ran(0, Started + "\n", string.Empty), "ps", "-o", "lstart=");

        Assert.True(await runtime.StillOursAsync(record, TestContext.Current.CancellationToken));
        Assert.True(await runtime.StillOursAsync(record with { Started = string.Empty }, TestContext.Current.CancellationToken));
        Assert.False(await runtime.StillOursAsync(record with { Started = "earlier" }, TestContext.Current.CancellationToken));
        Assert.False(await runtime.StillOursAsync(record with { Binary = "/other/otelcol" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_launch_with_no_process_is_not_ours()
    {
        _ = proc.Answer(new Ran(1, string.Empty, string.Empty), "ps");

        Assert.False(await Runtime().StillOursAsync(new Launched(7, Binary), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("\"otelcol.exe\",\"7\",\"Console\",\"1\",\"40,000 K\"\r\n", true)]
    [InlineData("\"OTELCOL.EXE\",\"7\"\r\n", true)]
    [InlineData("\"otelcol-contrib.exe\",\"7\"\r\n", false)]
    [InlineData("INFO: No tasks are running which match the specified criteria.\r\n", false)]
    public async Task On_windows_a_launch_is_ours_while_the_image_name_matches(string tasklist, bool ours)
    {
        _ = proc.Answer(new Ran(0, tasklist, string.Empty), "tasklist", "/FI", "PID eq 7");

        Assert.Equal(ours, await Runtime(windows: true).StillOursAsync(new Launched(7, @"C:\Users\me\AppData\otelcol.exe"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_recorded_pid_is_only_given_while_still_ours()
    {
        var runtime = Runtime();
        Assert.Null(await runtime.RecordedPidAsync(TestContext.Current.CancellationToken));

        Record(new Launched(7, Binary));
        Assert.Null(await runtime.RecordedPidAsync(TestContext.Current.CancellationToken));

        _ = proc.Answer(new Ran(0, Binary + "\n", string.Empty), "ps", "-o", "command=", "-p", "7");
        Assert.Equal(7, await runtime.RecordedPidAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_owner_is_whoever_lsof_names()
    {
        _ = proc.Answer(new Ran(0, "4242\n555\n", string.Empty), "lsof", "-nP", "-iTCP:4318", "-sTCP:LISTEN", "-t");
        _ = proc.Answer(new Ran(0, "/usr/bin/docker-proxy -host-port 4318\n", string.Empty), "ps", "-o", "command=", "-p", "4242");

        var owner = await Runtime().OwnerAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new Owner(4242, "/usr/bin/docker-proxy -host-port 4318"), owner);
        Assert.False(owner!.Ours);
    }

    [Fact]
    public async Task Without_lsof_the_listener_is_the_recorded_launch()
    {
        var runtime = Runtime();
        Record(new Launched(7, Binary));
        _ = proc.Answer(new Ran(0, Binary + "\n", string.Empty), "ps", "-o", "command=", "-p", "7");

        Assert.Equal(new Owner(7, Binary), await runtime.OwnerAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True((await runtime.OwnerAsync(cancellationToken: TestContext.Current.CancellationToken))!.Ours);

        _ = proc.Answer(new Ran(0, "lsof: garbage\n", string.Empty), "lsof");
        Assert.Equal(7, await runtime.ListenerPidAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Nobody_listening_has_no_owner()
    {
        _ = proc.Answer(new Ran(1, string.Empty, string.Empty), "lsof");

        Assert.Null(await Runtime().OwnerAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task On_windows_the_owner_comes_from_netstat()
    {
        var runtime = Runtime(windows: true);
        _ = proc.Answer(new Ran(0, "  TCP    0.0.0.0:4318    0.0.0.0:0    LISTENING    912\r\n", string.Empty), "netstat");
        _ = proc.Answer(new Ran(0, "\"com.docker.backend.exe\",\"912\"\r\n", string.Empty), "tasklist");

        Assert.Equal(new Owner(912, "com.docker.backend.exe"), await runtime.OwnerAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(912, await runtime.ListenerPidAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await runtime.OwnerAsync(4317, TestContext.Current.CancellationToken));
        Assert.Null(await runtime.ListenerPidAsync(4317, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("2048\n", 2.0)]
    [InlineData("  512\n", 0.5)]
    [InlineData("", null)]
    [InlineData("12k", null)]
    public async Task Resident_memory_comes_from_ps(string rss, double? megabytes)
    {
        _ = proc.Answer(new Ran(0, rss, string.Empty), "ps", "-o", "rss=", "-p", "7");

        Assert.Equal(megabytes, await Runtime().ResidentMbAsync(7, TestContext.Current.CancellationToken));
        Assert.Null(await Runtime(windows: true).ResidentMbAsync(7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stop_signals_only_a_launch_still_ours()
    {
        var runtime = Runtime();
        Assert.False(await runtime.StopRecordedAsync(TestContext.Current.CancellationToken));

        Record(new Launched(7, Binary));
        _ = proc.Answer(new Ran(0, Binary + "\n", string.Empty), "ps", "-o", "command=", "-p", "7");
        Assert.True(await runtime.StopRecordedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["kill", "-TERM", "7"], proc.Requests[^1].Argv);
        Assert.False(File.Exists(Paths.PidFile));

        Record(new Launched(8, Binary));
        Assert.False(await runtime.StopRecordedAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(proc.Requests, r => r.Argv[0] == "kill" && r.Argv[^1] == "8");
        Assert.False(File.Exists(Paths.PidFile));
    }

    [Fact]
    public async Task On_windows_stop_ends_the_process_with_taskkill()
    {
        Record(new Launched(7, @"C:\otel\otelcol.exe"));
        _ = proc.Answer(new Ran(0, "\"otelcol.exe\",\"7\"\r\n", string.Empty), "tasklist");

        Assert.True(await Runtime(windows: true).StopRecordedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["taskkill", "/PID", "7", "/F"], proc.Requests[^1].Argv);
    }

    [Fact]
    public void A_pid_file_that_is_not_a_record_reads_as_none()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.PidFile)!);
        File.WriteAllText(Paths.PidFile, "garbage\n");

        Assert.Null(Runtime().Recorded());
    }

    private OtelPaths Paths => new(temp.Path);

    private CollectorRuntime Runtime(bool windows = false) => new(Paths, proc, detached, windows);

    private void Record(Launched launched)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.PidFile)!);
        File.WriteAllText(Paths.PidFile, launched.Serialise());
    }

    private OtelInstaller Installer(byte[] download, HttpStatusCode answer = HttpStatusCode.OK)
    {
        body = download;
        status = answer;
        return new(http);
    }

    private static Pin PinOf(byte[] download, string member) =>
        new(new Uri("https://example.test/tool.tar.gz"), Sha(download), "tool", member);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] TarGz(params (string Name, byte[] Data, TarEntryType Type)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, data, type) in entries)
            {
                var format = type is TarEntryType.V7RegularFile ? TarEntryFormat.V7 : TarEntryFormat.Pax;
                TarEntry entry = format is TarEntryFormat.V7 ? new V7TarEntry(type, name) : new PaxTarEntry(type, name);
                if (type is TarEntryType.SymbolicLink)
                {
                    entry.LinkName = "elsewhere";
                }

                if (data.Length > 0)
                {
                    entry.DataStream = new MemoryStream(data);
                }

                writer.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    // Polls the log a detached process writes to, for up to ten seconds.
    private static async Task<string> Eventually(string log, Func<string, bool> done)
    {
        var text = string.Empty;
        for (var i = 0; i < 100; i++)
        {
            text = File.Exists(log) ? await File.ReadAllTextAsync(log) : string.Empty;
            if (done(text))
            {
                break;
            }

            await Task.Delay(100);
        }

        return text;
    }

    private sealed class FakeDetached : IDetachedStart
    {
        public const int Pid = 4242;

        public IReadOnlyList<string> Argv { get; private set; } = [];

        public string Log { get; private set; } = string.Empty;

        public Task<int> StartAsync(IReadOnlyList<string> argv, string log, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
        {
            Argv = argv;
            Log = log;
            return Task.FromResult(Pid);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
