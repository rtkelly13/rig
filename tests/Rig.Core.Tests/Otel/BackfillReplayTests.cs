using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using Rig.Core.Otel;

namespace Rig.Core.Tests.Otel;

/// <summary>
/// Backfill and replay, against an in-memory ClickHouse and a sender that never reaches a collector. The clock is
/// manual, so the settle window and the confirm wait run without waiting.
/// </summary>
public sealed class BackfillReplayTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly FakeClickHouse clickhouse = new();
    private readonly HttpClient http;
    private readonly FakeSender sender = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow.AddHours(1));
    private readonly Backfill backfill;
    private readonly string marks;

    public BackfillReplayTests()
    {
        http = new HttpClient(clickhouse, disposeHandler: false);
        sender.Lands = clickhouse;
        backfill = new Backfill(new ClickHouseClient(http, new Uri("http://clickhouse.test:1")), sender, clock, clock.SleepAsync);
        marks = Path.Join(temp.Path, "clickhouse", Backfill.StateFile);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private long Settled => TraceLines.Ns(clock.Now.AddHours(-2));

    private long Recent => TraceLines.Ns(clock.Now.AddSeconds(-10));

    public void Dispose()
    {
        http.Dispose();
        clickhouse.Dispose();
        temp.Dispose();
    }

    [Fact]
    public async Task A_sender_posts_json_to_the_signal_s_path()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        using var handler = new StubHandler(async request =>
        {
            seen = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        var problem = await new OtlpSender(client, new Uri("http://collector.test:1/")).SendAsync("logs", "{}", Ct);

        Assert.Null(problem);
        Assert.Equal("http://collector.test:1/v1/logs", seen!.RequestUri!.ToString());
        Assert.Equal("application/json", seen.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{}", body);
    }

    [Fact]
    public async Task A_rejected_batch_says_why_in_at_most_200_characters()
    {
        using var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(" " + new string('x', 300)) }));
        using var client = new HttpClient(handler);

        var problem = await new OtlpSender(client, new Uri("http://collector.test:1")).SendAsync("traces", "{}", Ct);

        Assert.Equal("collector rejected a batch: HTTP 400 " + new string('x', 200), problem);
    }

    [Fact]
    public async Task An_unreachable_collector_is_a_problem_not_an_exception()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var client = new HttpClient(handler);

        var problem = await new OtlpSender(client, new Uri("http://collector.test:1")).SendAsync("traces", "{}", Ct);

        Assert.Equal("collector unreachable at http://collector.test:1/v1/traces: connection refused", problem);
    }

    [Fact]
    public void A_mark_matches_to_a_tenth_of_a_microsecond()
    {
        var mark = new FileMark(10, 1_700_000_000_123_456_789);

        Assert.True(mark.Matches(new FileMark(10, 1_700_000_000_123_456_700)));
        Assert.False(mark.Matches(new FileMark(10, 1_700_000_000_123_456_600)));
        Assert.False(mark.Matches(new FileMark(11, 1_700_000_000_123_456_789)));
        Assert.False(mark.Matches(null));
    }

    [Fact]
    public void Default_roots_cover_every_place_rig_traces_to()
    {
        var baseDir = temp.Path;
        var runtime = temp.Folder("runtime");
        _ = temp.Folder("runtime/b");
        _ = temp.Folder("runtime/a");
        File.WriteAllText(Path.Join(runtime, "state.json"), """{"repo": "/primary"}""");

        var roots = Backfill.DefaultRoots(baseDir, "/checkout/temp/traces/sdk", "/override");

        Assert.Equal(
            [
                "/override",
                Path.Join(baseDir, "traces"),
                "/checkout/temp/traces/sdk",
                Path.Join("/primary", "temp", "traces", "sdk"),
                Path.Join(runtime, "a", "temp", "traces", "sdk"),
                Path.Join(runtime, "b", "temp", "traces", "sdk"),
                Path.Join(baseDir, "chrome-tabs", "traces"),
            ],
            roots);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""["/primary"]""")]
    [InlineData("""{"repo": 7}""")]
    [InlineData("""{"repo": ""}""")]
    public void Without_a_runtime_primary_the_roots_are_the_fixed_ones(string? state)
    {
        if (state is not null)
        {
            File.WriteAllText(temp.File("runtime/state.json"), state);
        }

        var roots = Backfill.DefaultRoots(temp.Path, "/checkout", overrideDir: "");

        Assert.Equal([Path.Join(temp.Path, "traces"), "/checkout", Path.Join(temp.Path, "chrome-tabs", "traces")], roots);
    }

    [Fact]
    public void Collector_trace_files_are_the_active_one_and_its_rotations()
    {
        Assert.Empty(Backfill.CollectorTraceFiles(Path.Join(temp.Path, "missing")));

        var data = temp.Folder("data");
        var active = temp.File("data/traces.jsonl");
        var rotated = temp.File("data/traces-2026-11-10T09-00-00.000.jsonl");
        _ = temp.File("data/logs.jsonl");

        Assert.Equal([rotated, active], Backfill.CollectorTraceFiles(data));
    }

    [Fact]
    public void Discovery_finds_each_ndjson_file_once()
    {
        var root = temp.Folder("traces");
        var day = temp.File("traces/2026/11/10.ndjson");
        var top = temp.File("traces/11.ndjson");
        _ = temp.File("traces/notes.txt");
        var extra = temp.File("collector/traces.jsonl");
        var roots = new List<string> { root, Path.Join(temp.Path, "missing") };
        if (!OperatingSystem.IsWindows())
        {
            var linked = temp.Folder("linked");
            _ = File.CreateSymbolicLink(Path.Join(linked, "same.ndjson"), day);
            roots.Add(linked);
        }

        var found = Backfill.Discover(roots, [extra, Path.Join(temp.Path, "gone.jsonl"), top]);

        Assert.Equal(new[] { day, top, extra }.Select(Real).Order(StringComparer.Ordinal), found);
    }

    [Fact]
    public void Marks_round_trip_in_the_python_s_layout()
    {
        Backfill.WriteMarks(marks, new Dictionary<string, FileMark>(StringComparer.Ordinal) { ["/b.ndjson"] = new(2, 20), ["/a.ndjson"] = new(1, 10) });

        Assert.Equal(
            """
            {
              "/a.ndjson": {
                "size": 1,
                "mtime_ns": 10
              },
              "/b.ndjson": {
                "size": 2,
                "mtime_ns": 20
              }
            }

            """.ReplaceLineEndings("\n"),
            File.ReadAllText(marks));
        Assert.Equal(new FileMark(1, 10), Backfill.ReadMarks(marks)["/a.ndjson"]);
        Assert.False(File.Exists(Path.ChangeExtension(marks, ".tmp")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"/a": 1}""")]
    [InlineData("""{"/a": {"size": 1}}""")]
    [InlineData("""{"/a": {"size": 1, "mtime_ns": 2}, "/b": {"mtime_ns": 2}}""")]
    public void Malformed_marks_are_none(string text)
    {
        File.WriteAllText(temp.File("marks.json"), text);

        Assert.Empty(Backfill.ReadMarks(Path.Join(temp.Path, "marks.json")));
    }

    [Fact]
    public void Missing_marks_are_none() =>
        Assert.Empty(Backfill.ReadMarks(marks));

    [Fact]
    public void A_mark_is_a_file_s_size_and_time()
    {
        Assert.Null(Backfill.Mark(Path.Join(temp.Path, "gone")));

        var file = temp.File("a.ndjson");
        File.WriteAllText(file, "abc");
        var mark = Backfill.Mark(file)!;

        Assert.Equal(3, mark.Size);
        Assert.Equal(TraceLines.Ns(File.GetLastWriteTimeUtc(file)), mark.MtimeNs);
    }

    [Fact]
    public async Task Missing_spans_are_sent_confirmed_and_the_file_marked()
    {
        _ = clickhouse.Spans.Add(new SpanKey("t1", "held"));
        var file = Traces("a.ndjson", TraceLines.Span("t1", "held", Settled), TraceLines.Span("t1", "new", Settled), TraceLines.Span("t2", "new", Settled));

        var report = await backfill.RunAsync([file], marks, cancellationToken: Ct);

        Assert.Equal([new FileResult(file, 3, 1, 2, 0, Confirmed: true)], report.Files);
        Assert.Equal(2, report.Sent);
        Assert.Empty(report.Errors);
        var (signal, body) = Assert.Single(sender.Sent);
        Assert.Equal("traces", signal);
        Assert.DoesNotContain("\"held\"", body, StringComparison.Ordinal);
        Assert.Equal(Backfill.Mark(file), Backfill.ReadMarks(marks)[Backfill.NormCase(file)]);
    }

    [Fact]
    public async Task An_unreadable_file_is_reported_left_unmarked_and_the_rest_still_run()
    {
        var locked = Traces("a.ndjson", TraceLines.Span("t1", "s1", Settled));
        var open = Traces("b.ndjson", TraceLines.Span("t2", "s2", Settled));

        BackfillReport report;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            report = await backfill.RunAsync([locked, open], marks, cancellationToken: Ct);
        }

        Assert.Equal([new FileResult(locked, 0, 0, 0, 0, Confirmed: false), new FileResult(open, 1, 0, 1, 0, Confirmed: true)], report.Files);
        Assert.StartsWith("a.ndjson: ", Assert.Single(report.Errors), StringComparison.Ordinal);
        Assert.False(Backfill.ReadMarks(marks).ContainsKey(Backfill.NormCase(locked)));

        var again = await backfill.RunAsync([locked, open], marks, cancellationToken: Ct);

        Assert.Equal([new FileResult(locked, 1, 0, 1, 0, Confirmed: true)], again.Files);
        Assert.Empty(again.Errors);
    }

    [Fact]
    public async Task A_marked_unchanged_file_is_skipped()
    {
        var file = Traces("a.ndjson", TraceLines.Span("t1", "s1", Settled));
        _ = await backfill.RunAsync([file], marks, cancellationToken: Ct);
        var requests = clickhouse.Requests.Count;

        var report = await backfill.RunAsync([file], marks, cancellationToken: Ct);

        Assert.Equal(1, report.Unchanged);
        Assert.Empty(report.Files);
        Assert.Equal(requests, clickhouse.Requests.Count);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_python_mark_with_whole_nanoseconds_still_matches()
    {
        var file = Traces("a.ndjson", TraceLines.Span("t1", "s1", Settled));
        var mark = Backfill.Mark(file)!;
        await File.WriteAllTextAsync(
            temp.File("clickhouse/" + Backfill.StateFile),
            string.Create(CultureInfo.InvariantCulture, $$$"""{"{{{Backfill.NormCase(file).Replace("\\", "\\\\", StringComparison.Ordinal)}}}": {"size": {{{mark.Size}}}, "mtime_ns": {{{mark.MtimeNs + 57}}}}}"""),
            Ct);

        var report = await backfill.RunAsync([file], marks, cancellationToken: Ct);

        Assert.Equal(1, report.Unchanged);
    }

    [Fact]
    public async Task Spans_that_ended_inside_the_settle_window_wait()
    {
        var file = Traces("a.ndjson", TraceLines.Span("t1", "old", Settled), TraceLines.Span("t1", "recent", Recent));

        var report = await backfill.RunAsync([file], marks, cancellationToken: Ct);

        Assert.Equal([new FileResult(file, 2, 0, 1, 1, Confirmed: false)], report.Files);
        Assert.False(File.Exists(marks));
    }

    [Fact]
    public async Task An_incomplete_span_settles_by_its_file_s_time()
    {
        var file = Traces("a.ndjson", TraceLines.Span("t1", "s1", Settled, incomplete: true));
        File.SetLastWriteTimeUtc(file, clock.Now.AddSeconds(-10).UtcDateTime);

        var waiting = await backfill.RunAsync([file], marks, cancellationToken: Ct);
        File.SetLastWriteTimeUtc(file, clock.Now.AddHours(-1).UtcDateTime);
        var ready = await backfill.RunAsync([file], marks, cancellationToken: Ct);

        Assert.Equal(1, waiting.Files[0].Unsettled);
        Assert.Equal(1, ready.Files[0].Sent);
    }

    [Fact]
    public async Task A_settle_window_can_be_shortened()
    {
        var file = Traces("a.ndjson", TraceLines.Span("t1", "s1", Recent));

        var report = await backfill.RunAsync([file], marks, settleSeconds: 0, cancellationToken: Ct);

        Assert.Equal(1, report.Sent);
    }

    [Fact]
    public async Task A_file_already_held_is_marked_without_sending()
    {
        _ = clickhouse.Spans.Add(new SpanKey("t1", "s1"));
        var file = Traces("a.ndjson", TraceLines.Span("t1", "s1", Settled));

        var report = await backfill.RunAsync([file], marks, cancellationToken: Ct);

        Assert.Equal([new FileResult(file, 1, 1, 0, 0, Confirmed: true)], report.Files);
        Assert.Empty(sender.Sent);
        Assert.True(File.Exists(marks));
    }

    [Fact]
    public async Task A_dry_run_counts_and_sends_and_marks_nothing()
    {
        var file = Traces("a.ndjson", TraceLines.Span("t1", "s1", Settled), TraceLines.Span("t1", "s2", Recent));

        var report = await backfill.RunAsync([file], marks, dryRun: true, cancellationToken: Ct);

        Assert.Equal([new FileResult(file, 2, 0, 1, 1, Confirmed: false)], report.Files);
        Assert.Empty(sender.Sent);
        Assert.False(File.Exists(marks));
    }

    [Fact]
    public async Task A_rejection_stops_the_run()
    {
        sender.RejectFrom = 0;
        var first = Traces("a.ndjson", TraceLines.Span("t1", "s1", Settled));
        var second = Traces("b.ndjson", TraceLines.Span("t2", "s2", Settled));

        var report = await backfill.RunAsync([first, second], marks, cancellationToken: Ct);

        Assert.Empty(report.Files);
        Assert.Equal(["collector rejected a batch: HTTP 400 no"], report.Errors);
        _ = Assert.Single(clickhouse.Requests, request => request.Query.ContainsKey("param_ids"));
    }

    [Fact]
    public async Task A_clickhouse_error_stops_the_run()
    {
        clickhouse.LookupError = "Code: 241. Memory limit exceeded";
        var file = Traces("a.ndjson", TraceLines.Span("t1", "s1", Settled));

        var report = await backfill.RunAsync([file, file], marks, cancellationToken: Ct);

        Assert.Equal(["Code: 241. Memory limit exceeded"], report.Errors);
        Assert.Empty(report.Files);
    }

    [Fact]
    public async Task Spans_that_never_land_leave_the_file_unmarked()
    {
        sender.Lands = null;
        var file = Traces("a.ndjson", TraceLines.Span("t1", "s1", Settled));

        var report = await backfill.RunAsync([file], marks, cancellationToken: Ct);

        Assert.Equal([new FileResult(file, 1, 0, 1, 0, Confirmed: false)], report.Files);
        Assert.Equal(15, clock.Slept.Count);
        Assert.All(clock.Slept, slept => Assert.Equal(TimeSpan.FromSeconds(2), slept));
        Assert.False(File.Exists(marks));
    }

    [Fact]
    public async Task A_file_gone_since_discovery_is_passed_over()
    {
        var report = await backfill.RunAsync([Path.Join(temp.Path, "gone.ndjson")], marks, cancellationToken: Ct);

        Assert.Empty(report.Files);
        Assert.Equal(0, report.Unchanged);
    }

    [Fact]
    public void Lines_split_as_python_s_splitlines_keeping_ends()
    {
        static string[] Split(string text) => [.. Replay.SplitLines(Encoding.UTF8.GetBytes(text)).Select(line => TraceLines.Utf8(line.ToArray()))];

        Assert.Equal(["a\r\n", "b\r", "c\n", "d"], Split("a\r\nb\rc\nd"));
        Assert.Equal(["\n", "\n"], Split("\n\n"));
        Assert.Equal(["x\r"], Split("x\r"));
        Assert.Empty(Split(string.Empty));
    }

    [Fact]
    public void Replayed_files_are_each_signal_s_active_one_and_rotations()
    {
        Assert.Empty(Replay.FilesFor("logs", Path.Join(temp.Path, "missing")));

        var logs = temp.File("data/logs.jsonl");
        var rotated = temp.File("data/logs-2026.jsonl");
        _ = temp.File("data/metrics.jsonl");

        Assert.Equal([rotated, logs], Replay.FilesFor("logs", Path.Join(temp.Path, "data")));
    }

    [Fact]
    public async Task Complete_lines_are_sent_once_and_a_torn_tail_waits()
    {
        var (data, state) = ReplayFolder();
        var logs = Write(data, "logs.jsonl", "{\"a\":1}\n  \n{\"b\":2}\r\n{\"tor");
        _ = Write(data, "metrics.jsonl", "{\"m\":1}\n");

        var first = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);
        await File.AppendAllTextAsync(logs, "n\":3}\n", Ct);
        var second = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);

        Assert.Equal([new ReplayResult(logs, 2), new ReplayResult(Path.Join(data, "metrics.jsonl"), 1)], first);
        Assert.Equal(1, second[0].Lines);
        Assert.Equal(0, second[1].Lines);
        Assert.Equal([("logs", "{\"a\":1}"), ("logs", "{\"b\":2}"), ("metrics", "{\"m\":1}"), ("logs", "{\"torn\":3}")], sender.Sent);
    }

    [Fact]
    public async Task A_rotated_file_keeps_its_offset()
    {
        var (data, state) = ReplayFolder();
        var logs = Write(data, "logs.jsonl", "{\"a\":1}\n");
        _ = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);

        File.Move(logs, Path.Join(data, "logs-2026.jsonl"));
        _ = Write(data, "logs.jsonl", "{\"c\":1}\n");
        _ = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);

        Assert.Equal(["{\"a\":1}", "{\"c\":1}"], sender.Sent.Select(sent => sent.Body));
    }

    [Fact]
    public async Task A_rejection_stops_with_the_offset_after_the_last_accepted_line()
    {
        var (data, state) = ReplayFolder();
        var logs = Write(data, "logs.jsonl", "{\"a\":1}\n{\"b\":2}\n");
        _ = Write(data, "metrics.jsonl", "{\"m\":1}\n");
        sender.RejectFrom = 1;

        var results = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);
        sender.RejectFrom = int.MaxValue;
        _ = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);

        Assert.Equal([new ReplayResult(logs, 1, "collector rejected a batch: HTTP 400 no")], results);
        Assert.Equal(["{\"a\":1}", "{\"b\":2}", "{\"m\":1}"], sender.Sent.Select(sent => sent.Body));
    }

    [Fact]
    public async Task Lines_accepted_before_a_cancellation_are_not_sent_again()
    {
        var (data, state) = ReplayFolder();
        _ = Write(data, "logs.jsonl", "{\"a\":1}\n{\"b\":2}\n");
        sender.CancelAt = 1;

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => new Replay(sender).RunAsync(data, state, cancellationToken: Ct));
        sender.CancelAt = int.MaxValue;
        _ = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], sender.Sent.Select(sent => sent.Body));
    }

    [Fact]
    public async Task A_dry_run_counts_and_records_nothing()
    {
        var (data, state) = ReplayFolder();
        var logs = Write(data, "logs.jsonl", "{\"a\":1}\n{\"b\":2}\n");

        var results = await new Replay(sender).RunAsync(data, state, dryRun: true, cancellationToken: Ct);

        Assert.Equal([new ReplayResult(logs, 2)], results);
        Assert.Empty(sender.Sent);
        Assert.False(File.Exists(state));
    }

    [Fact]
    public async Task Offsets_of_files_that_have_gone_are_dropped()
    {
        var (data, state) = ReplayFolder();
        var logs = Write(data, "logs.jsonl", "{\"a\":1}\n");
        ReplayOffsets.Write(state, new Dictionary<string, ReplayOffset>(StringComparer.Ordinal) { ["1"] = new(1, 5) });

        _ = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);

        var inode = FileIds.Inode(logs)!.Value;
        Assert.Equal([inode.ToString(CultureInfo.InvariantCulture)], ReplayOffsets.Read(state).Keys);
        Assert.Equal(new ReplayOffset(inode, 8, ReplayOffsets.HeadHash("{\"a\":1}\n"u8, 8)), ReplayOffsets.Read(state).Values.Single());
    }

    [Fact]
    public async Task A_python_written_offset_is_honoured()
    {
        var (data, state) = ReplayFolder();
        var raw = "{\"a\":1}\n{\"b\":2}\n"u8.ToArray();
        var logs = Write(data, "logs.jsonl", Encoding.UTF8.GetString(raw));
        var inode = FileIds.Inode(logs)!.Value;
        await File.WriteAllTextAsync(state, string.Create(CultureInfo.InvariantCulture, $$$"""{"{{{inode}}}": {"inode": {{{inode}}}, "sent": 8, "head": "{{{ReplayOffsets.HeadHash(raw, 8)}}}"}}"""), Ct);

        _ = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);

        Assert.Equal(["{\"b\":2}"], sender.Sent.Select(sent => sent.Body));
    }

    [Fact]
    public async Task A_later_file_on_the_same_inode_is_replayed_from_its_start()
    {
        var (data, state) = ReplayFolder();
        var logs = Write(data, "logs.jsonl", "{\"x\":1}\n{\"y\":2}\n");
        var inode = FileIds.Inode(logs)!.Value;
        ReplayOffsets.Write(state, new Dictionary<string, ReplayOffset>(StringComparer.Ordinal) { [inode.ToString(CultureInfo.InvariantCulture)] = new(inode, 8, new string('0', 64)) });

        _ = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);

        Assert.Equal(["{\"x\":1}", "{\"y\":2}"], sender.Sent.Select(sent => sent.Body));
    }

    [Theory]
    [InlineData("18446744073709551615", 18446744073709551615UL)]
    [InlineData("\"7\"", 7UL)]
    [InlineData("-1", null)]
    [InlineData("1.5", null)]
    public void An_offset_s_inode_is_unsigned_and_read_leniently(string inode, ulong? expected)
    {
        var offsets = ReplayOffsets.Parse("{\"k\": {\"inode\": " + inode + ", \"sent\": 3}}");

        Assert.Equal(expected, offsets.GetValueOrDefault("k")?.Inode);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_file_stops_the_run_with_its_name()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only: file modes.");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root reads a file whatever its mode.");
        var (data, state) = ReplayFolder();
        var logs = Write(data, "logs.jsonl", "{\"a\":1}\n");
        _ = Write(data, "metrics.jsonl", "{\"m\":1}\n");
        File.SetUnixFileMode(logs, UnixFileMode.None);
        try
        {
            var results = await new Replay(sender).RunAsync(data, state, cancellationToken: Ct);

            var result = Assert.Single(results);
            Assert.StartsWith("logs.jsonl: ", result.Error, StringComparison.Ordinal);
            Assert.Empty(sender.Sent);
        }
        finally
        {
            File.SetUnixFileMode(logs, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // Its resolved path, as discovery reports it (the temp folder is itself behind a link on macOS).
    private static string Real(string path) => Backfill.Discover([], [path]).Single();

    private static string Write(string folder, string name, string text)
    {
        var path = Path.Join(folder, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private (string Data, string State) ReplayFolder() => (temp.Folder("data"), Path.Join(temp.Path, ReplayOffsets.StateFile));

    private string Traces(string name, params string[] lines)
    {
        var path = temp.File("traces/" + name);
        File.WriteAllText(path, string.Concat(lines));
        File.SetLastWriteTimeUtc(path, clock.Now.AddHours(-1).UtcDateTime);
        return path;
    }
}
