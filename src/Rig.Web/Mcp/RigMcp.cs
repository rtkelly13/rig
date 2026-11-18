using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Rig.Web.Mcp;

/// <summary>
/// Registers the rig ops MCP server over <see cref="RigOps"/>: the HTTP transport in <see cref="RigWebHost"/> and the
/// stdio transport <c>rig web mcp</c> serves. Both expose the same eleven tools, so an agent sees one server however
/// it connects.
/// </summary>
public static class RigMcp
{
    /// <summary>The instructions an MCP client receives on initialize.</summary>
    public const string Instructions =
        "The rig ops data: the jobs coordinator, the estate decision feed, generous-worktree trim candidates, disk headroom "
        + "and recent traces. Each tool answers the JSON its /api/ops route answers. trim_candidates scans in the background: "
        + "call it again while scanning is true, then apply_trim only the paths it offered.";

    /// <summary>Registers <paramref name="ops"/> and the tools found in this assembly, without a transport.</summary>
    public static IMcpServerBuilder AddRigMcp(this IServiceCollection services, RigOps ops)
    {
        ArgumentNullException.ThrowIfNull(services);
        _ = services.AddSingleton(ops);
        return services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = RigOps.ServerName, Version = RigOps.ServerVersion };
                options.ServerInstructions = Instructions;
            })
            .WithToolsFromAssembly(typeof(RigOpsTools).Assembly);
    }

    /// <summary>
    /// Serves the tools over stdio until the client closes the stream or <paramref name="cancellationToken"/> fires.
    /// Nothing but MCP messages goes to stdout: logging is left off, and diagnostics go to <paramref name="error"/>.
    /// </summary>
    public static async Task<int> RunStdioAsync(RigOps ops, TextWriter error, CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        _ = builder.Logging.ClearProviders();
        _ = builder.Services.AddRigMcp(ops).WithStdioServerTransport();
        using var host = builder.Build();
        try
        {
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (IOException e)
        {
            await error.WriteLineAsync($"rig web mcp: {e.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
