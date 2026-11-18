using System.Net;
using Rig.Core.Otel;
using Rig.Core.Tests.Otel;
using Rig.Web.Ops;

namespace Rig.Web.Tests;

/// <summary>A trace list row from ClickHouse is read strictly, and one that is not is a ClickHouse error the list falls back from.</summary>
public sealed class TraceRowTests
{
    private const string Row = """{"trace_id":"t","service":"s","root":"r","start_ns":1,"end_ns":2,"spans":3,"errors":4}""";

    [Theory]
    [InlineData("""{"trace_id":null,"service":"s","root":"r","start_ns":1,"end_ns":2,"spans":3,"errors":4}""", "$.trace_id: expected a string, found null")]
    [InlineData("""{"trace_id":"t","service":"s","root":"r","start_ns":"1","end_ns":2,"spans":3,"errors":4}""", "$.start_ns: expected a whole number, found the string \"1\"")]
    [InlineData("""{"trace_id":"t","service":"s","root":"r","start_ns":1,"end_ns":2,"spans":3.5,"errors":4}""", "$.spans: expected a whole number, found the number 3.5")]
    [InlineData("""{"trace_id":"t","service":"s","root":"r","start_ns":1,"end_ns":2,"spans":3}""", "$.errors: expected a whole number, found nothing")]
    [InlineData("""{"service":"s","root":"r","start_ns":1,"end_ns":2,"spans":3,"errors":4}""", "$.trace_id: expected a string, found nothing")]
    public async Task A_row_that_is_not_a_trace_is_a_clickhouse_error(string row, string problem)
    {
        using var answer = Answer(row);
        using var http = new HttpClient(answer);

        var error = await Assert.ThrowsAsync<ClickHouseException>(() => Telemetry.ClickHouse(new ClickHouseClient(http), new TraceQuery()));

        Assert.Equal("ClickHouse returned rows that could not be read: " + problem, error.Message);
    }

    [Fact]
    public async Task A_repeated_member_keeps_its_last_value_and_the_ones_not_read_are_ignored()
    {
        using var answer = Answer(Row.Replace("\"service\":\"s\"", "\"service\":\"a\",\"service\":\"b\",\"extra\":[1]", StringComparison.Ordinal));
        using var http = new HttpClient(answer);

        var trace = Assert.Single(await Telemetry.ClickHouse(new ClickHouseClient(http), new TraceQuery()));

        Assert.Equal("b", trace.Service);
    }

    private static StubHandler Answer(string body) =>
        new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
}
