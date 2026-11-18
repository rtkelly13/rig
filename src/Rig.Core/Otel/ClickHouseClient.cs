using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Rig.Core.Text;

namespace Rig.Core.Otel;

/// <summary>ClickHouse answered with an error (bad SQL), or did not answer at all.</summary>
public sealed class ClickHouseException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ClickHouseException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ClickHouseException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and its cause.</summary>
    public ClickHouseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A span's identity: what backfill compares so a span is never sent twice.</summary>
/// <param name="TraceId">The trace.</param>
/// <param name="SpanId">The span within it.</param>
public sealed record SpanKey(string TraceId, string SpanId);

/// <summary>One <c>otel</c> table's active parts.</summary>
/// <param name="Table">Its name.</param>
/// <param name="Rows">Its rows.</param>
/// <param name="BytesOnDisk">Its size on disk.</param>
public sealed record TableStats(string Table, ulong Rows, ulong BytesOnDisk);

/// <summary>
/// The reads of the on-demand ClickHouse that the stack and backfill need, over its HTTP interface; Values are bound by ClickHouse to <c>{name:Type}</c> placeholders, never spliced into SQL.
/// </summary>
/// <param name="http">Sends the requests; its timeout is the caller's (30 seconds by default).</param>
/// <param name="baseUrl">The server, <see cref="LocalUrl"/> by default.</param>
public sealed class ClickHouseClient(HttpClient http, Uri? baseUrl = null)
{
    /// <summary>The loopback server <c>clickhouse up</c> starts.</summary>
    public static readonly Uri LocalUrl = new($"http://{ClickHouseConfig.Host}:{ClickHouseConfig.HttpPort.ToString(CultureInfo.InvariantCulture)}");

    private const string HasTableSql = "SELECT count() FROM system.tables WHERE database = {database:String} AND name = {table:String}";

    private const string TableStatsSql = """
        SELECT name AS table,
               toUInt64(ifNull(total_rows, 0)) AS rows,
               toUInt64(ifNull(total_bytes, 0)) AS bytes_on_disk
        FROM system.tables WHERE database = {database:String} AND engine LIKE '%MergeTree'
        ORDER BY bytes_on_disk DESC, table
        """;

    private const string ExistingSpansSql = "SELECT DISTINCT TraceId, SpanId FROM otel.otel_traces WHERE TraceId IN {ids:Array(String)}";

    // The user `default` with an empty password, as httpx's auth=("default", "").
    private static readonly AuthenticationHeaderValue Auth = new("Basic", Convert.ToBase64String("default:"u8));

    private readonly Uri server = baseUrl ?? LocalUrl;

    /// <summary>A ClickHouse string literal, for the elements of an array parameter.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    }

    /// <summary>Runs <paramref name="sql"/> and returns the response body as is, in whatever FORMAT the SQL names.</summary>
    /// <exception cref="ClickHouseException">ClickHouse is unreachable or answered with an error.</exception>
    public async Task<string> TextAsync(string sql, IReadOnlyDictionary<string, string>? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var query = new StringBuilder("?output_format_json_quote_64bit_integers=0&date_time_output_format=iso");
        foreach (var (name, value) in parameters ?? new Dictionary<string, string>())
        {
            _ = query.Append("&param_").Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "/" + query.ToString()));
        request.Headers.Authorization = Auth;
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(sql));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ClickHouseException($"ClickHouse unreachable at {server}: {e.Message}", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var text = body.Trim();
                throw new ClickHouseException(text.Length > 0 ? text : $"HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}");
            }

            return body;
        }
    }

    /// <summary>Whether the server answers <c>SELECT 1</c>.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await TextAsync("SELECT 1", cancellationToken: cancellationToken).ConfigureAwait(false)).Trim() == "1";
        }
        catch (ClickHouseException)
        {
            return false;
        }
    }

    /// <summary>Whether the <c>otel</c> database holds <paramref name="table"/>; false when ClickHouse cannot say.</summary>
    public async Task<bool> HasTableAsync(string table, CancellationToken cancellationToken = default)
    {
        try
        {
            return await TableExistsAsync(table, cancellationToken).ConfigureAwait(false);
        }
        catch (ClickHouseException)
        {
            return false;
        }
    }

    /// <summary>Rows and disk per <c>otel</c> table, largest first; an empty table is listed with zeros.</summary>
    /// <exception cref="ClickHouseException">ClickHouse is unreachable or answered with an error.</exception>
    public async Task<IReadOnlyList<TableStats>> TableStatsAsync(CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["database"] = ClickHouseConfig.Database };
        var rows = await RowsAsync(
            TableStatsSql,
            parameters,
            ClickHouseRowContext.Default.TableRow,
            cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => new TableStats(row.Table, row.Rows, row.BytesOnDisk))];
    }

    /// <summary>Which spans of <paramref name="traceIds"/> ClickHouse already holds; none before the table exists.</summary>
    /// <exception cref="ClickHouseException">ClickHouse is unreachable or answered with an error.</exception>
    public async Task<IReadOnlySet<SpanKey>> ExistingSpansAsync(IEnumerable<string> traceIds, int chunk = 500, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunk);
        var ids = traceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var found = new HashSet<SpanKey>();
        // An error here must not read as "no table": every span would then look missing and be sent again.
        if (ids.Count == 0 || !await TableExistsAsync("otel_traces", cancellationToken).ConfigureAwait(false))
        {
            return found;
        }

        foreach (var batch in ids.Chunk(chunk))
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["ids"] = "[" + string.Join(", ", batch.Select(Quote)) + "]" };
            var keys = await RowsAsync(
                ExistingSpansSql,
                parameters,
                ClickHouseRowContext.Default.SpanRow,
                cancellationToken).ConfigureAwait(false);
            found.UnionWith(keys.Select(row => new SpanKey(row.TraceId, row.SpanId)));
        }

        return found;
    }

    private async Task<bool> TableExistsAsync(string table, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["database"] = ClickHouseConfig.Database, ["table"] = table };
        return (await TextAsync(HasTableSql, parameters, cancellationToken).ConfigureAwait(false)).Trim() == "1";
    }

    /// <summary>Runs <paramref name="sql"/> (without a FORMAT clause) as JSONEachRow and reads each non-blank line as a <typeparamref name="TRow"/>.</summary>
    /// <exception cref="ClickHouseException">ClickHouse is unreachable, answered with an error, or returned a row that does not read as <typeparamref name="TRow"/>.</exception>
    public async Task<List<TRow>> RowsAsync<TRow>(string sql, IReadOnlyDictionary<string, string> parameters, JsonTypeInfo<TRow> row, CancellationToken cancellationToken = default)
        where TRow : class
    {
        ArgumentNullException.ThrowIfNull(sql);
        var body = await TextAsync(sql.TrimEnd().TrimEnd(';') + "\nFORMAT JSONEachRow", parameters, cancellationToken).ConfigureAwait(false);
        try
        {
            return [.. body.Split('\n').Where(line => line.Trim().Length > 0).Select(line => JsonProblem.Read(line, row))];
        }
        catch (JsonInputException e)
        {
            throw new ClickHouseException($"ClickHouse returned rows that could not be read: {e.Message}", e);
        }
    }
}

/// <summary>The compile-time metadata for ClickHouse's JSONEachRow rows: snake_case names, every member read is required and typed.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Strict,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    AllowDuplicateProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TableRow))]
[JsonSerializable(typeof(SpanRow))]
internal sealed partial class ClickHouseRowContext : JsonSerializerContext;

internal sealed record TableRow(string Table, ulong Rows, ulong BytesOnDisk);

internal sealed record SpanRow([property: JsonPropertyName("TraceId")] string TraceId, [property: JsonPropertyName("SpanId")] string SpanId);
