using Rig.Core.Processes;

namespace Rig.Core.Tests.Fakes;

/// <summary>
/// An <see cref="IGitHubApi"/> that answers by method and route, as <c>gh api</c> would: a known route gets its
/// JSON, an unknown one <c>HTTP 404</c>. Every call is recorded.
/// </summary>
public sealed class FakeGitHub : IGitHubApi
{
    private readonly Dictionary<(string Method, string Route), Ran> _routes = [];

    /// <summary>Every call, in the order it was made.</summary>
    public List<ApiRequest> Calls { get; } = [];

    /// <summary>Answers <paramref name="method"/> <paramref name="route"/> with <paramref name="json"/>.</summary>
    public FakeGitHub On(string method, string route, string json)
    {
        _routes[(method, route)] = new Ran(0, json, string.Empty);
        return this;
    }

    /// <summary>Fails <paramref name="method"/> <paramref name="route"/> with an HTTP <paramref name="status"/>.</summary>
    public FakeGitHub Fail(string method, string route, int status)
    {
        _routes[(method, route)] = new Ran(1, string.Empty, $"gh: HTTP {status}");
        return this;
    }

    /// <summary>Fails <paramref name="method"/> <paramref name="route"/> with <paramref name="error"/> on stderr, as gh prints it.</summary>
    public FakeGitHub Fail(string method, string route, string error)
    {
        _routes[(method, route)] = new Ran(1, string.Empty, error);
        return this;
    }

    /// <inheritdoc />
    public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Calls.Add(request);
        return Task.FromResult(
            _routes.TryGetValue((request.Method, request.Route), out var answer)
                ? answer
                : new Ran(1, string.Empty, "gh: Not Found (HTTP 404)"));
    }
}
