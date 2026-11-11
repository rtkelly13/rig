using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Vault;

namespace Rig.Core.Otel;

/// <summary>Posts OTLP/JSON to a collector.</summary>
public interface IOtlpSender
{
    /// <summary>Sends <paramref name="body"/> as <paramref name="signal"/> (traces, logs, metrics); null when accepted, else why not.</summary>
    Task<string?> SendAsync(string signal, string body, CancellationToken cancellationToken = default);
}

/// <summary>POSTs OTLP/JSON to <c>{baseUrl}/v1/{signal}</c>.</summary>
/// <param name="http">Sends the requests.</param>
/// <param name="baseUrl">The collector, e.g. the contrib collector on <see cref="ClickHouseConfig.CollectorPort"/>.</param>
public sealed class OtlpSender(HttpClient http, Uri baseUrl) : IOtlpSender
{
    /// <inheritdoc/>
    public async Task<string?> SendAsync(string signal, string body, CancellationToken cancellationToken = default)
    {
        var url = $"{baseUrl.OriginalString.TrimEnd('/')}/v1/{signal}";
        using var content = new StringContent(body, new UTF8Encoding(false), "application/json");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(new Uri(url), content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return $"collector unreachable at {url}: {e.Message}";
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            return $"collector rejected a batch: HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} {text[..Math.Min(text.Length, 200)]}";
        }
    }
}

/// <summary>A file's size and modification time when it was last confirmed done.</summary>
/// <param name="Size">Its size in bytes.</param>
/// <param name="MtimeNs">Its modification time in nanoseconds since the epoch.</param>
public sealed record FileMark(long Size, long MtimeNs)
{
    /// <summary>
    /// Whether <paramref name="other"/> is the same mark. .NET reads modification times to 100 ns, the Python to 1 ns, so
    /// the times are compared to 100 ns: a mark the Python wrote still matches, and a mismatch only re-checks a file.
    /// </summary>
    public bool Matches(FileMark? other) => other is not null && other.Size == Size && other.MtimeNs / 100 == MtimeNs / 100;
}

/// <summary>What backfill did with one file.</summary>
/// <param name="Path">The file.</param>
/// <param name="Spans">Distinct spans in it.</param>
/// <param name="Present">How many ClickHouse already held.</param>
/// <param name="Sent">How many were sent (or would be, on a dry run).</param>
/// <param name="Unsettled">How many ended too recently to send yet.</param>
/// <param name="Confirmed">Whether every span is now confirmed in ClickHouse, so the file is marked done.</param>
public sealed record FileResult(string Path, int Spans, int Present, int Sent, int Unsettled, bool Confirmed);

/// <summary>A backfill run: each file checked, how many were unchanged since done, and what stopped it.</summary>
/// <param name="Files">The files checked.</param>
/// <param name="Unchanged">Files skipped as unchanged since they were marked done.</param>
/// <param name="Errors">What stopped the run, if anything.</param>
public sealed record BackfillReport(IReadOnlyList<FileResult> Files, int Unchanged, IReadOnlyList<string> Errors)
{
    /// <summary>Spans sent across every file.</summary>
    public int Sent => Files.Sum(file => file.Sent);
}

/// <summary>
/// Replays recorded spans into ClickHouse through the contrib collector; ported from <c>rig.otel_stack.backfill</c>.
/// Spans are compared with <c>otel.otel_traces</c> by (TraceId, SpanId), so a span is never sent twice, even after the
/// collector renames its active file. Of a span written twice the complete copy wins. Spans that ended inside the
/// settle window wait for the next run. A file is marked done (by size and mtime) only once every span in it is
/// confirmed in ClickHouse, never on the strength of a 200 alone.
/// </summary>
/// <param name="clickhouse">Says which spans are already held.</param>
/// <param name="sender">Sends to the contrib collector.</param>
/// <param name="time">The clock spans settle against, and the confirm wait.</param>
/// <param name="sleep">Pauses between confirm checks.</param>
public sealed class Backfill(ClickHouseClient clickhouse, IOtlpSender sender, TimeProvider time, Func<TimeSpan, CancellationToken, Task> sleep)
{
    /// <summary>Spans that ended this recently wait for the next run.</summary>
    public const int SettleSeconds = 300;

    /// <summary>How long to wait for a sent batch to land before leaving the file for next run.</summary>
    public const int ConfirmSeconds = 30;

    /// <summary>Overrides where rig's tracer writes; read as one more root.</summary>
    public const string TraceDirEnv = "SHARED_UTILITIES_TRACE_DIR";

    /// <summary>The marks file's name, under the ClickHouse folder.</summary>
    public const string StateFile = "backfill-state.json";

    private static readonly TimeSpan ConfirmPoll = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Every folder rig's tracer writes day files to: the override, the per-user folder, the checkout, the primary
    /// runtime copy's checkout, every runtime copy, and the Chrome tab organiser's.
    /// </summary>
    /// <param name="baseDir">rig's per-user folder.</param>
    /// <param name="checkoutTraces">This checkout's <c>temp/traces/sdk</c>.</param>
    /// <param name="overrideDir">The <see cref="TraceDirEnv"/> folder, if set.</param>
    public static IReadOnlyList<string> DefaultRoots(string baseDir, string checkoutTraces, string? overrideDir)
    {
        var runtime = Path.Join(baseDir, "runtime");
        List<string> roots = [];
        if (!string.IsNullOrEmpty(overrideDir))
        {
            roots.Add(overrideDir);
        }

        roots.Add(Path.Join(baseDir, "traces"));
        roots.Add(checkoutTraces);
        if (PrimaryRepo(Path.Join(runtime, "state.json")) is { Length: > 0 } repo)
        {
            roots.Add(Path.Join(repo, "temp", "traces", "sdk"));
        }

        if (Directory.Exists(runtime))
        {
            roots.AddRange(Directory.EnumerateDirectories(runtime).Order(PyText.CodePointOrder).Select(copy => Path.Join(copy, "temp", "traces", "sdk")));
        }

        roots.Add(Path.Join(baseDir, "chrome-tabs", "traces"));
        return roots;
    }

    /// <summary>The always-on collector's trace files: the active <c>traces.jsonl</c> and its rotated copies.</summary>
    public static IReadOnlyList<string> CollectorTraceFiles(string folder) =>
        Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "traces*.jsonl").Order(PyText.CodePointOrder)] : [];

    /// <summary>The NDJSON files under <paramref name="roots"/> plus <paramref name="extra"/> files, each once (links and repeats collapse).</summary>
    public static IReadOnlyList<string> Discover(IEnumerable<string> roots, IEnumerable<string> extra)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(extra);
        var recurse = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        List<string> candidates = [];
        foreach (var root in roots)
        {
            if (Directory.Exists(root))
            {
                candidates.AddRange(Directory.EnumerateFiles(root, "*.ndjson", recurse).Order(PyText.CodePointOrder));
            }
        }

        candidates.AddRange(extra.Where(File.Exists));
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in candidates)
        {
            var resolved = ObsidianOrphans.RealPath(path);
            _ = seen.TryAdd(NormCase(resolved), resolved);
        }

        return [.. seen.Values.Order(PyText.CodePointOrder)];
    }

    /// <summary>The marks in <paramref name="path"/>; none when it is missing, unreadable or malformed.</summary>
    public static Dictionary<string, FileMark> ReadMarks(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var marks = new Dictionary<string, FileMark>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || LaxJson.Int(LaxJson.Field(entry.Value, "size")) is not { } size
                    || LaxJson.Int(LaxJson.Field(entry.Value, "mtime_ns")) is not { } mtime)
                {
                    return [];
                }

                marks[entry.Name] = new FileMark(size, mtime);
            }

            return marks;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes <paramref name="marks"/> sorted, two-space indented, through a temporary file.</summary>
    public static void WriteMarks(string path, IReadOnlyDictionary<string, FileMark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        var w = new PyJsonWriter();
        _ = w.Object(() =>
        {
            foreach (var key in marks.Keys.Order(PyText.CodePointOrder))
            {
                _ = w.Key(key).Object(() => w.Key("size").Number(marks[key].Size).Key("mtime_ns").Number(marks[key].MtimeNs));
            }
        });
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = Path.ChangeExtension(path, ".tmp");
        File.WriteAllText(temporary, w.ToString() + "\n", new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The size and modification time of <paramref name="path"/>, or null when it has gone.</summary>
    public static FileMark? Mark(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileMark(info.Length, MtimeNs(info)) : null;
    }

    /// <summary>Sends what ClickHouse lacks from each of <paramref name="files"/>, marking each file done once confirmed.</summary>
    /// <param name="files">The files, as <see cref="Discover"/> gives them.</param>
    /// <param name="statePath">The marks file.</param>
    /// <param name="settleSeconds">Spans that ended this recently wait for the next run.</param>
    /// <param name="dryRun">Count what would be sent; send and mark nothing.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<BackfillReport> RunAsync(
        IReadOnlyList<string> files,
        string statePath,
        int settleSeconds = SettleSeconds,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var marks = ReadMarks(statePath);
        List<FileResult> results = [];
        List<string> errors = [];
        var unchanged = 0;
        foreach (var path in files)
        {
            var key = NormCase(path);
            if (Mark(path) is not { } mark)
            {
                continue;
            }

            if (mark.Matches(marks.GetValueOrDefault(key)))
            {
                unchanged++;
                continue;
            }

            FileResult result;
            try
            {
                var outcome = await FileAsync(path, settleSeconds, dryRun, cancellationToken).ConfigureAwait(false);
                if (outcome.Problem is { } problem)
                {
                    errors.Add(problem);
                    if (outcome.Result is null)
                    {
                        break;
                    }
                }

                result = outcome.Result!;
            }
            catch (ClickHouseException e)
            {
                errors.Add(e.Message);
                break;
            }

            results.Add(result);
            if (result.Confirmed && !dryRun)
            {
                marks[key] = mark;
                WriteMarks(statePath, marks);
            }
        }

        return new BackfillReport(results, unchanged, errors);
    }

    /// <summary>Python's <c>os.path.normcase</c>: lower case with backslashes on Windows, unchanged elsewhere.</summary>
    public static string NormCase(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return OperatingSystem.IsWindows() ? path.Replace('/', '\\').ToLowerInvariant() : path;
    }

    private static long MtimeNs(FileInfo info) => (info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100;

    private static string? PrimaryRepo(string state)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(state, Encoding.UTF8));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && LaxJson.Field(document.RootElement, "repo") is { ValueKind: JsonValueKind.String } repo
                ? repo.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task<(FileResult? Result, string? Problem)> FileAsync(string path, int settleSeconds, bool dryRun, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unread is not empty: left unconfirmed, so the next run reads it again; the files after it still run.
            return (new FileResult(path, 0, 0, 0, 0, Confirmed: false), $"{Path.GetFileName(path)}: {e.Message}");
        }

        var spans = TraceSpans.BestCopies(TraceSpans.Parse(text));
        var present = await clickhouse.ExistingSpansAsync(spans.Select(span => span.TraceId), cancellationToken: cancellationToken).ConfigureAwait(false);
        var settleFrom = ((time.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) * 100) - (settleSeconds * 1_000_000_000L);
        var fileMtime = MtimeNs(new FileInfo(path));
        var missing = spans.Where(span => !present.Contains(new SpanKey(span.TraceId, span.SpanId))).ToList();
        var ready = missing.Where(span => (span.Incomplete ? fileMtime : span.EndNs) < settleFrom).ToList();
        var unsettled = missing.Count - ready.Count;
        var held = spans.Count - missing.Count;
        if (dryRun)
        {
            return (new FileResult(path, spans.Count, held, ready.Count, unsettled, Confirmed: false), null);
        }

        if (ready.Count == 0)
        {
            return (new FileResult(path, spans.Count, held, 0, unsettled, Confirmed: missing.Count == 0), null);
        }

        foreach (var body in TraceSpans.Envelopes(ready))
        {
            if (await sender.SendAsync("traces", Render(body), cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return (null, problem);
            }
        }

        // Wait for what was just sent to land even when the file cannot be marked done yet: a later file holding the
        // same spans must see them present, not send them again.
        var landed = await ConfirmAsync(ready, cancellationToken).ConfigureAwait(false);
        return (new FileResult(path, spans.Count, held, ready.Count, unsettled, Confirmed: unsettled == 0 && landed), null);
    }

    private async Task<bool> ConfirmAsync(IReadOnlyList<FileSpan> sent, CancellationToken cancellationToken)
    {
        var wanted = sent.Select(span => new SpanKey(span.TraceId, span.SpanId)).ToHashSet();
        var begun = time.GetTimestamp();
        while (true)
        {
            var held = await clickhouse.ExistingSpansAsync(wanted.Select(key => key.TraceId), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (wanted.IsSubsetOf(held))
            {
                return true;
            }

            if (time.GetElapsedTime(begun) >= TimeSpan.FromSeconds(ConfirmSeconds))
            {
                return false;
            }

            await sleep(ConfirmPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    // json.dumps of the envelope: compact separators would differ from the Python's ", " and ": ", but the collector
    // parses either; what matters is the same spans in the same envelope.
    private static string Render(JsonObject body) => body.ToJsonString();
}
