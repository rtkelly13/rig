using System.CommandLine;
using System.Globalization;
using Rig.Core;
using Rig.Core.Otel;
using Rig.Web;

namespace Rig.Cli.Web;

/// <summary>Where <c>rig web</c> finds what it serves.</summary>
/// <param name="Checkout">The rig checkout: the frontend under <c>src/rig/web_host/templates</c> and the brand images under <c>assets</c>.</param>
/// <param name="RegistryFile">The managed-projects registry the estate cards are read from.</param>
/// <param name="WorkingDirectory">The folder rig was run from.</param>
/// <param name="DbPath">The shared SQLite database.</param>
internal sealed record WebPlaces(string Checkout, string RegistryFile, string WorkingDirectory, string DbPath)
{
    /// <summary>The built frontend.</summary>
    public string TemplatesDir => Path.Join(Checkout, "src", "rig", "web_host", "templates");

    /// <summary>The brand images served under <c>/media/assets/</c>.</summary>
    public string AssetsRoot => Path.Join(Checkout, "assets");
}

/// <summary>
/// <c>rig web</c>: runs the dashboard. The ASP.NET host owns the port and answers the routes ported to C#; the rest go to
/// the Python routes, which it starts as a child on a free loopback port and stops when it stops (<see cref="PythonRoutes"/>).
/// The subcommands (<c>mcp</c>, <c>install</c>, <c>ensure</c>, <c>uninstall</c>, <c>backup</c>, <c>migrate</c>,
/// <c>db-status</c>) and <c>rig web --help</c> still run on the Python CLI. Exit codes: 0 stopped, 2 could not listen.
/// </summary>
/// <param name="places">What it serves.</param>
/// <param name="startPython">Starts the Python CLI with these arguments as a child the host owns.</param>
/// <param name="probe">Asks whether the child is listening yet.</param>
/// <param name="openBrowser">Opens a page in the browser.</param>
/// <param name="delay">Waits between probes.</param>
/// <param name="output">Where the banner goes.</param>
/// <param name="error">Where problems go.</param>
internal sealed class WebCommand(
    WebPlaces places,
    Func<IReadOnlyList<string>, IChildServer> startPython,
    IPortProbe probe,
    Action<Uri> openBrowser,
    Func<TimeSpan, CancellationToken, Task> delay,
    TextWriter output,
    TextWriter error)
{
    /// <summary>How long the Python routes get to start listening.</summary>
    public static readonly TimeSpan PythonStart = TimeSpan.FromSeconds(60);

    /// <summary>The <c>rig web</c> command.</summary>
    public Command Build()
    {
        var port = new Option<int>("--port", "-p") { Description = "Port to bind the web server to.", DefaultValueFactory = _ => WebHostSettings.ProductionPort };
        var host = new Option<string>("--host") { Description = "Host address to bind to.", DefaultValueFactory = _ => "127.0.0.1" };
        var open = new Option<bool>("--open") { Description = "Open in browser on launch (the default).", DefaultValueFactory = _ => true };
        var noOpen = new Option<bool>("--no-open") { Description = "Do not open the browser." };
        var preview = new Option<bool>("--preview") { Description = "Run as preview environment (defaults to port 8091)." };
        var web = new Command("web", "Rig web dashboard & visual studio. Its subcommands still run on the Python CLI.") { port, host, open, noOpen, preview };
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
        var settings = new WebHostSettings(
            host,
            WebHostSettings.EffectivePort(port, preview),
            preview,
            places.TemplatesDir,
            places.AssetsRoot,
            places.DbPath,
            places.RegistryFile,
            places.WorkingDirectory);
        var python = new PythonRoutes(
            childPort => startPython(["web", "upstream", "--port", childPort.ToString(CultureInfo.InvariantCulture)]),
            probe,
            delay);
        await using (python.ConfigureAwait(false))
        {
            if (await python.StartAsync(PythonStart, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                await error.WriteLineAsync($"rig web: {problem}; only the routes ported to C# will answer.").ConfigureAwait(false);
            }

            using var forwarding = RigWebHost.ForwardingClient();
            var server = RigWebHost.Create(settings, forwarding, () => python.Address);
            await using (server.ConfigureAwait(false))
            {
                return await ServeAsync(server, settings, open, cancellationToken).ConfigureAwait(false);
            }
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
            // Ctrl+C: the host and the Python routes stop as the usings unwind.
        }

        await output.WriteLineAsync("Stopping server...").ConfigureAwait(false);
        return (int)ExitCode.Clean;
    }
}
