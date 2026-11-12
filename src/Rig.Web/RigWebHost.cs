using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rig.Core.Connectivity;
using Rig.Core.Processes;

namespace Rig.Web;

/// <summary>
/// The rig web host: one ASP.NET minimal-API server on a loopback port, serving the React app in <c>web/</c> and the
/// JSON routes it calls. Paths it does not own yet go to the Python routes through <see cref="Forwarder"/>.
/// </summary>
/// <remarks>
/// Built from the empty builder, so nothing but <see cref="WebHostSettings"/> chooses the address: not
/// <c>ASPNETCORE_URLS</c>, <c>launchSettings.json</c> or an <c>appsettings</c> file.
/// </remarks>
public sealed class RigWebHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RigWebHost(WebApplication app) => _app = app;

    /// <summary>The address the host is bound to; valid once <see cref="StartAsync"/> has returned.</summary>
    public Uri Address
    {
        get
        {
            var bound = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException("The web host is not started.");
            return new Uri(bound.Replace("[::]", "localhost", StringComparison.Ordinal), UriKind.Absolute);
        }
    }

    /// <summary>
    /// Builds the host. <paramref name="upstream"/> answers where the Python routes listen, or null while they are not
    /// running; <paramref name="forwarding"/> is the client that reaches them.
    /// </summary>
    public static RigWebHost Create(WebHostSettings settings, HttpClient forwarding, Func<Uri?> upstream)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(forwarding);
        ArgumentNullException.ThrowIfNull(upstream);
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            if (IPAddress.TryParse(settings.Host, out var address))
            {
                kestrel.Listen(address, settings.Port);
            }
            else
            {
                kestrel.ListenLocalhost(settings.Port);
            }
        });
        builder.Services.AddRoutingCore();
        var app = builder.Build();
        var host = new RigWebHost(app);
        app.UseRouting();
        HostRoutes.Map(app, settings, () => host.Address.Port);
        FrontendRoutes.Map(app, settings);
        var git = new Git(new SystemProc(), settings.CodeRoot);
        ConnectivityRoutes.Map(app, settings, new EstateGraph(git, TimeProvider.System), Path.Join(settings.CodeRoot, "personal"));
        var forwarder = new Forwarder(forwarding, upstream);
        app.Map("/{**path}", forwarder.ForwardAsync);
        return host;
    }

    /// <summary>
    /// The client the forwarder reaches the Python routes with: no redirects followed, no cookies kept, no proxy and no
    /// decompression, so an answer comes back to the browser exactly as Python sent it. No timeout either: a trim scan
    /// can run for minutes, and the browser's own request bounds it.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The client owns its handler and disposes it.")]
    public static HttpClient ForwardingClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>Starts listening.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => _app.StartAsync(cancellationToken);

    /// <summary>Stops listening, letting requests in flight finish.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => _app.StopAsync(cancellationToken);

    /// <summary>Completes when the host stops: on <paramref name="cancellationToken"/>, or when the process is asked to stop.</summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => _app.WaitForShutdownAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
