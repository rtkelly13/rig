using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Processes;

public sealed class GhApiTests
{
    private readonly FakeProc _proc = new FakeProc().Answer(new Ran(0, "[]", string.Empty), "gh", "api");

    [Fact]
    public async Task A_get_is_a_plain_gh_api_call()
    {
        var ran = await new GhApi(_proc).CallAsync(ApiRequest.Get("repos/o/r/labels"), TestContext.Current.CancellationToken);

        Assert.Equal("[]", ran.Out);
        var request = Assert.Single(_proc.Requests);
        Assert.Equal(["gh", "api", "repos/o/r/labels"], request.Argv);
        Assert.Null(request.StdinText);
        Assert.Equal(GhApi.DefaultTimeout, request.Timeout);
    }

    [Fact]
    public async Task A_write_sends_its_body_on_stdin()
    {
        await new GhApi(_proc).CallAsync(new ApiRequest("PATCH", "repos/o/r", """{"a":1}"""), TestContext.Current.CancellationToken);

        var request = Assert.Single(_proc.Requests);
        Assert.Equal(["gh", "api", "-X", "PATCH", "repos/o/r", "--input", "-"], request.Argv);
        Assert.Equal("""{"a":1}""", request.StdinText);
    }

    [Fact]
    public async Task A_write_without_a_body_sends_nothing()
    {
        await new GhApi(_proc).CallAsync(new ApiRequest("DELETE", "repos/o/r/labels/x"), TestContext.Current.CancellationToken);

        Assert.Equal(["gh", "api", "-X", "DELETE", "repos/o/r/labels/x"], Assert.Single(_proc.Requests).Argv);
    }

    [Fact]
    public async Task The_fake_answers_by_method_and_route()
    {
        var github = new FakeGitHub().On("GET", "repos/o/r", """{"name":"r"}""").Fail("DELETE", "repos/o/r", 403);

        Assert.Equal("""{"name":"r"}""", (await github.CallAsync(ApiRequest.Get("repos/o/r"), TestContext.Current.CancellationToken)).Out);
        Assert.Contains("HTTP 403", (await github.CallAsync(new ApiRequest("DELETE", "repos/o/r"), TestContext.Current.CancellationToken)).Err, StringComparison.Ordinal);
        Assert.Contains("HTTP 404", (await github.CallAsync(ApiRequest.Get("nope"), TestContext.Current.CancellationToken)).Err, StringComparison.Ordinal);
        Assert.Equal(3, github.Calls.Count);
    }

    [Fact]
    public async Task The_fake_proc_answers_the_latest_matching_prefix()
    {
        var proc = new FakeProc().Answer(new Ran(0, "first", string.Empty), "git").Answer(new Ran(0, "second", string.Empty), "git");

        Assert.Equal("second", (await proc.RunAsync(new ProcRequest(["git", "x"]), TestContext.Current.CancellationToken)).Out);
        Assert.Equal(Ran.NotFound, (await proc.RunAsync(new ProcRequest(["gh"]), TestContext.Current.CancellationToken)).Code);
    }
}
