using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Rig.Cli.Tests.Web;

/// <summary><c>rig web mcp</c>: a real MCP stdio exchange, with nothing but protocol messages on stdout.</summary>
public sealed class WebMcpTests
{
    [Fact]
    public async Task Stdio_answers_initialize_and_lists_the_eleven_tools()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Join(AppContext.BaseDirectory, "rig.dll"));
        start.ArgumentList.Add("web");
        start.ArgumentList.Add("mcp");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start");

        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
        await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        var initialized = await ReadAsync(process, id: 1, TestContext.Current.CancellationToken);

        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
        await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        var listed = await ReadAsync(process, id: 2, TestContext.Current.CancellationToken);

        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal("rig-ops", (string?)initialized["result"]!["serverInfo"]!["name"]);
        Assert.Equal(
            "apply_trim, attention, decisions, disk_usage, list_jobs, list_runs, list_traces, run_output, start_run, telemetry_status, trim_candidates",
            string.Join(", ", listed["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.ToString()).Order(StringComparer.Ordinal)));
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
    }

    // Reads stdout until the answer to `id`; a line that is not JSON is a protocol failure, not a skipped line.
    private static async Task<JsonNode> ReadAsync(Process process, int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                ?? throw new InvalidOperationException($"rig web mcp exited before answering {id}");
            var message = JsonNode.Parse(line) ?? throw new InvalidOperationException($"not JSON: {line}");
            if ((int?)message["id"] == id)
            {
                return message;
            }
        }
    }
}
