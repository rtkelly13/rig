namespace Rig.Core.Processes;

/// <summary>One GitHub REST call: <c>GET repos/o/r/labels</c>, or a write whose JSON body is already encoded.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Route">The route, as <c>gh api</c> takes it.</param>
/// <param name="Body">The encoded JSON body, or null.</param>
public sealed record ApiRequest(string Method, string Route, string? Body = null)
{
    /// <summary>A GET of <paramref name="route"/>.</summary>
    public static ApiRequest Get(string route) => new("GET", route);
}

/// <summary>
/// The GitHub REST seam. Production uses <see cref="GhApi"/>; tests use a fake that answers by method and route.
/// A failed call answers as <c>gh api</c> does: a non-zero code and <c>HTTP &lt;status&gt;</c> in the error text.
/// </summary>
public interface IGitHubApi
{
    /// <summary>Runs one REST call.</summary>
    Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>REST calls through <c>gh api</c>; a body goes on standard input as <c>--input -</c>.</summary>
/// <param name="proc">The process runner.</param>
public sealed class GhApi(IProc proc) : IGitHubApi
{
    /// <summary>The default timeout for one gh call.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> argv = request.Method == "GET"
            ? ["gh", "api", request.Route]
            : ["gh", "api", "-X", request.Method, request.Route];
        if (request.Body is not null)
        {
            argv.AddRange(["--input", "-"]);
        }

        return proc.RunAsync(
            new ProcRequest(argv) { Timeout = DefaultTimeout, StdinText = request.Body },
            cancellationToken);
    }
}
