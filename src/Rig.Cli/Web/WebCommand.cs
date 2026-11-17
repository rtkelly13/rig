#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using Rig.Core;
using Rig.Core.Roots;
using Rig.Web;
using Rig.Web.Mcp;

namespace Rig.Cli.Web;

/// <summary>Where <c>rig web</c> finds what it serves.</summary>
/// <param name="Checkout">The rig checkout: the frontend under <c>src/rig/web_host/templates</c>, the C4 viewer under <c>src/Rig.Web/C4/static</c> and the brand images under <c>assets</c>.</param>
/// <param name="RegistryFile">The managed-projects registry the estate cards are read from.</param>
/// <param name="WorkingDirectory">The folder rig was run from.</param>
/// <param name="DbPath">The shared SQLite database.</param>
/// <param name="CodeRoot">The code root whose <c>personal/</c> checkouts the estate graph reads.</param>
internal sealed record WebPlaces(string Checkout, string RegistryFile, string WorkingDirectory, string DbPath, string CodeRoot)
{
    /// <summary>The built frontend.</summary>
    public string TemplatesDir => Path.Join(Checkout, "src", "rig", "web_host", "templates");

    /// <summary>The C4 viewer's page and static files.</summary>
    public string C4StaticDir => Path.Join(Checkout, "src", "Rig.Web", "C4", "static");

    /// <summary>The sample C4 bundle the viewer opens first; the source checkout ships it, a runtime copy may not.</summary>
    public string C4SampleBundle => Path.Join(Checkout, "experiments", "c4-okf", "rig");

    /// <summary>The brand images served under <c>/media/assets/</c>.</summary>
    public string AssetsRoot => Path.Join(Checkout, "assets");

    /// <summary>This checkout's own trace files, one of the roots telemetry reads.</summary>
    public string TracesDir => Path.Join(Checkout, "temp", "traces", "sdk");

    /// <summary>The triage queue's Markdown projection, beside the registry's checkout rather than in it.</summary>
    public string QueuePath =>
        Path.Join(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(RegistryFile))) ?? WorkingDirectory, "temp", "triage", "queue.md");
}

/// <summary>
/// <c>rig web</c>: runs the dashboard. The ASP.NET host owns the port and answers every route.
/// The service and database subcommands (<c>install</c>, <c>ensure</c>, <c>uninstall</c>, <c>backup</c>, <c>migrate</c>,
/// <c>db-status</c>) are aliases for <c>rig tools</c>; <c>mcp</c> serves the ops tools over stdio.
/// Exit codes: 0 stopped, 2 could not listen.
/// </summary>
/// <param name="places">What it serves.</param>
/// <param name="openBrowser">Opens a page in the browser.</param>
/// <param name="output">Where the banner goes.</param>
/// <param name="error">Where problems go.</param>
/// <param name="ops">The data behind the MCP tools; null builds this machine's real sources.</param>
/// <param name="aliases">The legacy subcommands that forward to <c>rig tools</c>.</param>
internal sealed class WebCommand(
    WebPlaces places,
    Action<Uri> openBrowser,
    TextWriter output,
    TextWriter error,
    RigOps? ops = null,
    IReadOnlyList<Command>? aliases = null)
{
    private readonly Lazy<RigOps> _ops = new(() => ops ?? RigOps.Default());

    /// <summary>The <c>rig web</c> command.</summary>
    public Command Build()
    {
        var port = new Option<int>("--port", "-p") { Description = "Port to bind the web server to.", DefaultValueFactory = _ => WebHostSettings.ProductionPort };
        var host = new Option<string>("--host") { Description = "Host address to bind to.", DefaultValueFactory = _ => "127.0.0.1" };
        var open = new Option<bool>("--open") { Description = "Open in browser on launch (the default).", DefaultValueFactory = _ => true };
        var noOpen = new Option<bool>("--no-open") { Description = "Do not open the browser." };
        var preview = new Option<bool>("--preview") { Description = "Run as preview environment (defaults to port 8091)." };
        var mcp = new Command("mcp", "Serve the ops tools over stdio for an MCP client (Claude Desktop, Cursor, Orca).");
        mcp.SetAction((_, ct) => RigMcp.RunStdioAsync(_ops.Value, error, ct));
        var web = new Command("web", "Rig web dashboard & visual studio. Service and database subcommands are aliases for `rig tools`.")
        {
            port, host, open, noOpen, preview, mcp,
        };
        foreach (var alias in aliases ?? [])
        {
            web.Subcommands.Add(alias);
        }

        web.SetAction((parsed, ct) => RunAsync(
            parsed.GetValue(host)!,
            parsed.GetValue(port),
            parsed.GetValue(preview),
            parsed.GetValue(open) && !parsed.GetValue(noOpen),
            ct));
        return web;
    }

    private async Task<int> RunAsync(string host, int port, bool preview, bool open, CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var settings = new WebHostSettings(
            host,
            WebHostSettings.EffectivePort(port, preview),
            preview,
            places.TemplatesDir,
            places.AssetsRoot,
            places.DbPath,
            places.RegistryFile,
            places.WorkingDirectory,
            Path.Join(CodeRoot.Current(), CodeRoot.PersonalFolder),
            Rig.Core.Estate.Decisions.DefaultFeedPath(Environment.GetEnvironmentVariable("LOCALAPPDATA"), home),
            places.QueuePath,
            places.TracesDir,
            places.CodeRoot,
            places.C4StaticDir,
            places.C4SampleBundle);
        var server = await RigWebHost.CreateAsync(settings, _ops.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (server.ConfigureAwait(false))
        {
            return await ServeAsync(server, settings, open, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> ServeAsync(RigWebHost server, WebHostSettings settings, bool open, CancellationToken cancellationToken)
    {
        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            await error.WriteLineAsync($"rig web: could not listen on {settings.Host}:{settings.Port}: {e.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        await output.WriteLineAsync($"[{settings.Environment} MODE] Rig Web Dashboard running at {server.Address}").ConfigureAwait(false);
        if (open)
        {
            try
            {
                openBrowser(server.Address);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                // No browser (a headless box, a service): the dashboard still serves, at the address in the banner.
                await error.WriteLineAsync($"rig web: could not open a browser: {e.Message}").ConfigureAwait(false);
            }
        }

        try
        {
            await server.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: the host stops as the using unwinds.
        }

        await output.WriteLineAsync("Stopping server...").ConfigureAwait(false);
        return (int)ExitCode.Clean;
    }
}
