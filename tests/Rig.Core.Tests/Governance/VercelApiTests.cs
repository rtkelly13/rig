using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>The Vercel REST client and the token it calls with.</summary>
public sealed class VercelApiTests : IDisposable
{
    private const double Now = 1_800_000_000;

    private readonly TempDir _home = new();
    private readonly FakeProc _proc = new();

    public void Dispose() => _home.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string MacAuth => Path.Join(_home.Folder("Library/Application Support/com.vercel.cli"), "auth.json");

    private string XdgAuth => Path.Join(_home.Folder(".config/com.vercel.cli"), "auth.json");

    private VercelToken Resolver(string? fromEnvironment = null) =>
        new(name => name == "VERCEL_TOKEN" ? fromEnvironment : null, _home.Path, _proc, new FixedTime(DateTimeOffset.FromUnixTimeSeconds((long)Now)));

    [Theory]
    [InlineData(null, true)]
    [InlineData(0d, true)]
    [InlineData(Now + 61, true)]
    [InlineData(Now + 59, false)]
    [InlineData((Now + 3600) * 1000, true)]
    [InlineData((Now - 1) * 1000, false)]
    public void A_token_is_live_until_a_minute_before_it_expires(double? expiresAt, bool live) =>
        Assert.Equal(live, VercelToken.IsLive(new("t", expiresAt), Now));

    [Theory]
    [InlineData("""{"token": "t", "expiresAt": 5}""", "t", 5d)]
    [InlineData("""{"token": 3, "expiresAt": "soon"}""", null, null)]
    public void The_auth_file_is_read_leniently(string json, string? token, double? expiresAt)
    {
        File.WriteAllText(MacAuth, json);

        Assert.Equal(new VercelCliAuth(token, expiresAt), VercelToken.Read(MacAuth));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{")]
    public void An_auth_file_that_is_not_an_object_reads_as_none(string json)
    {
        File.WriteAllText(MacAuth, json);

        Assert.Null(VercelToken.Read(MacAuth));
        Assert.Null(VercelToken.Read(Path.Join(_home.Path, "missing.json")));
    }

    [Fact]
    public async Task The_environment_wins()
    {
        await File.WriteAllTextAsync(MacAuth, """{"token": "cli"}""", Token);

        Assert.Equal("env", await Resolver("env").ResolveAsync(Token));
    }

    [Fact]
    public async Task A_live_cli_token_is_used_in_file_order()
    {
        await File.WriteAllTextAsync(XdgAuth, """{"token": "xdg"}""", Token);
        Assert.Equal("xdg", await Resolver(string.Empty).ResolveAsync(Token));

        await File.WriteAllTextAsync(MacAuth, """{"token": "mac"}""", Token);
        Assert.Equal("mac", await Resolver().ResolveAsync(Token));
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task An_expired_token_is_rotated_by_the_cli_and_read_again()
    {
        await File.WriteAllTextAsync(MacAuth, """{"token": "rotated", "expiresAt": 1}""", Token);
        _ = _proc.Answer(new Ran(0, string.Empty, string.Empty), "vercel", "whoami");

        Assert.Equal("rotated", await Resolver().ResolveAsync(Token));
        Assert.Equal(["vercel", "whoami"], Assert.Single(_proc.Requests).Argv);
    }

    [Fact]
    public async Task Without_the_cli_an_expired_token_falls_through_to_the_next_file()
    {
        await File.WriteAllTextAsync(MacAuth, """{"token": "old", "expiresAt": 1}""", Token);

        Assert.Equal(
            "no usable Vercel token: `vercel login` or set VERCEL_TOKEN",
            (await Assert.ThrowsAsync<VercelApiException>(() => Resolver().ResolveAsync(Token))).Message);

        await File.WriteAllTextAsync(XdgAuth, """{"token": "xdg"}""", Token);
        Assert.Equal("xdg", await Resolver().ResolveAsync(Token));
    }

    [Fact]
    public async Task A_get_sends_the_token_once_resolved_and_returns_the_json()
    {
        using var http = new FakeHttp();
        http.Routes["https://api.vercel.com/v6/deployments?teamId=t%201&limit=2"] = """{"deployments": []}""";
        var resolved = 0;
        using var client = http.Client();
        var api = new VercelApi(client, _ => Task.FromResult($"tok{++resolved}"));

        var body = await api.GetAsync("/v6/deployments", [new("teamId", "t 1"), new("limit", "2")], Token);
        _ = await api.GetAsync("/v6/deployments", [new("teamId", "t 1"), new("limit", "2")], Token);

        Assert.Equal("""{"deployments":[]}""", body!.ToJsonString());
        Assert.Equal(1, resolved);
    }

    [Fact]
    public async Task Failures_become_vercel_errors()
    {
        using var http = new FakeHttp();
        http.Routes["https://api.vercel.com/v9/projects"] = "not json";
        using var client = http.Client();
        var api = new VercelApi(client, _ => Task.FromResult("tok"));

        Assert.Equal("Vercel /v9/projects returned a body that is not JSON", (await Assert.ThrowsAsync<VercelApiException>(() => api.GetAsync("/v9/projects", [], Token))).Message);
        Assert.Equal("Vercel /v2/user answered HTTP 404", (await Assert.ThrowsAsync<VercelApiException>(() => api.GetAsync("/v2/user", [], Token))).Message);
    }

    [Fact]
    public async Task A_transport_failure_becomes_a_vercel_error()
    {
        using var handler = new Unreachable();
        using var client = new HttpClient(handler);
        var api = new VercelApi(client, _ => Task.FromResult("tok"));

        Assert.Equal("Vercel /v9/projects failed: down", (await Assert.ThrowsAsync<VercelApiException>(() => api.GetAsync("/v9/projects", [], Token))).Message);
        Assert.Equal("Bearer tok", handler.Authorization);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Unreachable : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            throw new HttpRequestException("down");
        }
    }
}
