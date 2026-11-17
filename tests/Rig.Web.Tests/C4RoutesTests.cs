using System.Net;
using System.Text.RegularExpressions;
using Rig.Core.Tests;
using Rig.Web.Tests.Fakes;

namespace Rig.Web.Tests;

/// <summary>
/// The C4 viewer's page and JSON, answered as the Python <c>c4_okf</c> server answered them. <c>Recorded/c4/responses</c>
/// holds that server's answers, byte for byte, to every request against <c>Recorded/c4/hard</c>.
/// </summary>
public sealed partial class C4RoutesTests : IAsyncLifetime
{
    private readonly EstateTree _tree = new();
    private readonly TempDir _checkout = new();
    private RigWebHost? _host;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var samples = Path.Join(_checkout.Path, "experiments", "c4-okf");
        CopyDirectory(Path.Join(AppContext.BaseDirectory, "Recorded", "c4", "hard"), Path.Join(samples, "hard"));
        Put("experiments/c4-okf/rig/a.md", "---\ntype: C4 Context\ntitle: A\n---\n");
        Put("experiments/c4-okf/parquet-sourcegenerator/b.md", "---\ntype: C4 Context\ntitle: B\n---\n");
        Put("src/Rig.Web/C4/static/index.html", "<title>C4</title>");
        Put("src/Rig.Web/C4/static/c4.js", "console.log(1);");
        Put("src/Rig.Web/C4/static/c4.css", "body{}");
        Put("src/Rig.Web/C4/static/vendor/x.bin", "x");
        Put("outside.txt", "secret");
        var settings = _tree.Settings() with
        {
            C4StaticDir = Path.Join(_checkout.Path, "src", "Rig.Web", "C4", "static"),
            C4SampleBundle = Path.Join(samples, "hard"),
        };
        _host = await RigWebHost.CreateAsync(settings);
        await _host.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = _host.Address };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _tree.Dispose();
        _checkout.Dispose();
    }

    public static TheoryData<string> Recordings()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Join(AppContext.BaseDirectory, "Recorded", "c4", "responses"), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task Answers_what_the_python_server_answered(string recording)
    {
        var text = await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Recorded", "c4", "responses", recording), TestContext.Current.CancellationToken);
        var split = text.IndexOf('\n', StringComparison.Ordinal);
        var head = text[..split];
        var expected = text[(split + 1)..];
        var path = Regex.Match(head, "\"path\": \"([^\"]*)\"").Groups[1].Value;
        var status = int.Parse(Regex.Match(head, "\"status\": (\\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        using var response = await GetAsync(path);
        var actual = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoCache);
        // The recording was made beside the full sample set; the bundle list is the one thing that differs by layout.
        Assert.Equal(Bundles().Replace(expected, "\"bundles\":[]"), Bundles().Replace(actual, "\"bundles\":[]"));
    }

    [Fact]
    public async Task Offers_the_sample_bundle_first_then_its_siblings()
    {
        using var response = await GetAsync("/api/c4/bundle");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("\"bundles\":[\"hard\",\"parquet-sourcegenerator\",\"rig\"]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_bundle_is_served_and_an_unknown_one_is_refused()
    {
        using var named = await GetAsync("/api/c4/bundle?bundle=rig");
        using var unknown = await GetAsync("/api/c4/bundle?bundle=..%2Frig");

        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        Assert.Contains("\"name\":\"rig\"", await named.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("{\"error\":\"no bundle `../rig`\"}", await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_concept_and_an_unknown_endpoint_are_404_json()
    {
        using var concept = await GetAsync("/api/c4/concept?id=nope");
        using var endpoint = await GetAsync("/api/c4/nope");

        Assert.Equal(HttpStatusCode.NotFound, concept.StatusCode);
        Assert.Equal("{\"error\":\"no concept `nope`\"}", await concept.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, endpoint.StatusCode);
        Assert.Equal("{\"error\":\"unknown endpoint /api/c4/nope\"}", await endpoint.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Notes_never_pass_raw_html_or_script_links_through()
    {
        using var response = await GetAsync("/api/c4/concept?id=ctx");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\\\"javascript", body, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", body, StringComparison.Ordinal);
        Assert.Contains("style=\\\"text-align:right\\\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/c4")]
    [InlineData("/c4/")]
    [InlineData("/c4/static/index.html")]
    public async Task Serves_the_viewer_page(string path)
    {
        using var response = await GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("<title>C4</title>", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Theory]
    [InlineData("c4.js", "text/javascript; charset=utf-8")]
    [InlineData("c4.css", "text/css; charset=utf-8")]
    [InlineData("vendor/x.bin", "application/octet-stream")]
    public async Task Static_files_carry_their_content_type(string file, string type)
    {
        using var response = await GetAsync("/c4/static/" + file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.ToString());
    }

    [Theory]
    [InlineData("/c4/static/missing.js")]
    [InlineData("/c4/static/..%2F..%2F..%2F..%2Foutside.txt")]
    [InlineData("/c4/static/%2e%2e/%2e%2e/%2e%2e/%2e%2e/outside.txt")]
    public async Task A_missing_or_escaping_static_path_is_404_json(string path)
    {
        using var response = await GetAsync(path);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
    }

    [GeneratedRegex("\"bundles\":\\[[^\\]]*\\]")]
    private static partial Regex Bundles();

    private void Put(string relative, string text) => File.WriteAllText(_checkout.File(relative), text);

    private Task<HttpResponseMessage> GetAsync(string path) =>
        _client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    private static void CopyDirectory(string from, string to)
    {
        _ = Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Join(to, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(directory, Path.Join(to, Path.GetFileName(directory)));
        }
    }
}
