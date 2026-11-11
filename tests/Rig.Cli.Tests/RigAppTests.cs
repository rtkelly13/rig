using Rig.Cli.Agent;
using Rig.Cli.Forwarding;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests;

public sealed class RigAppTests : IDisposable
{
    private const string Checkout = "/code/personal/rig";
    private static readonly string[] Uv = ["uv", "run", "--no-sync", "--project", Checkout, "rig"];

    private readonly FakeProc _proc = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private bool _fromPython;

    private Task<int> Run(params string[] args) =>
        new RigApp(
            new PythonRig(_proc, Checkout, _fromPython),
            new FakeGitHub(),
            _ => Task.FromResult<RepoSlug?>(null),
            _out,
            _err,
            [() => new Rig.Cli.Models.ModelsCommand(new Rig.Cli.Models.ModelDiscovery(_proc, "/home", _ => null), _out).Build()],
            new AgentLauncher(_proc, new AgentHost("/home/none.json", "/work", p => $"/bin/{p}"), _out, _err))
            .RunAsync(args, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("workspace", "sync", "--dry-run")]
    [InlineData("estate", "--help")]
    public async Task A_python_command_goes_over_whole_and_attached(params string[] args)
    {
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "uv");

        Assert.Equal(0, await Run(args));

        var request = Assert.Single(_proc.Requests);
        Assert.Equal([.. Uv, .. args], request.Argv);
        Assert.True(request.Attached);
    }

    [Theory]
    [InlineData(new string[0], new[] { "/bin/claude", "--model", "opus" })]
    [InlineData(new[] { "-ccc", "--resume" }, new[] { "/bin/claude", "--model", "opus", "--resume" })]
    [InlineData(new[] { "agent", "-codex", "exec", "--help" }, new[] { "/bin/codex", "exec", "--help" })]
    [InlineData(new[] { "--print-config" }, null)]
    public async Task An_agent_launch_goes_to_the_launcher_unparsed(string[] args, string[]? launched)
    {
        _proc.Answer(new Ran(3, string.Empty, string.Empty), "/bin/claude").Answer(new Ran(3, string.Empty, string.Empty), "/bin/codex");

        Assert.Equal(launched is null ? 0 : 3, await Run(args));

        Assert.Equal(launched, _proc.Requests.SingleOrDefault()?.Argv);
        Assert.DoesNotContain(_proc.Requests, r => r.Argv[0] == "uv");
    }

    [Theory]
    [InlineData("agent", "models", "-a", "codex")]
    [InlineData("agent", "--models", "-a", "codex")]
    public async Task Agent_models_lists_the_models(params string[] args)
    {
        Assert.Equal(0, await Run(args));

        Assert.Contains("gpt-6.1-sol", _out.ToString(), StringComparison.Ordinal);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Help_lists_the_agent_command()
    {
        Assert.Equal(0, await Run("--help"));

        Assert.Contains(AgentLauncher.Description, _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_python_exit_code_comes_back_unchanged()
    {
        _proc.Answer(new Ran(1, string.Empty, string.Empty), "uv");

        Assert.Equal(1, await Run("sync"));
    }

    [Fact]
    public async Task Missing_uv_is_an_error_that_says_why()
    {
        _proc.Answer(new Ran(Ran.NotFound, string.Empty, "uv is not on PATH"), "uv");

        Assert.Equal(2, await Run("sync"));
        Assert.StartsWith("rig: uv is not on PATH.", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_127_from_python_itself_is_passed_through()
    {
        _proc.Answer(new Ran(Ran.NotFound, string.Empty, string.Empty), "uv");

        Assert.Equal(Ran.NotFound, await Run("sync"));
        Assert.Empty(_err.ToString());
    }

    [Fact]
    public async Task Help_comes_from_csharp_and_lists_the_python_commands()
    {
        Assert.Equal(0, await Run("--help"));

        Assert.Empty(_proc.Requests);
        var help = _out.ToString();
        Assert.Contains(RigApp.Description, help, StringComparison.Ordinal);
        foreach (var (name, _) in Forwarded.Commands)
        {
            Assert.Contains($"  {name} ", help, StringComparison.Ordinal);
        }

        Assert.Contains("(Python)", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_comes_from_csharp()
    {
        Assert.Equal(0, await Run("--version"));

        Assert.Empty(_proc.Requests);
        Assert.NotEmpty(_out.ToString().Trim());
    }

    [Fact]
    public async Task An_unknown_command_is_an_error_not_a_forward()
    {
        Assert.Equal(2, await Run("no-such-command"));

        Assert.Empty(_proc.Requests);
        Assert.Contains("no-such-command", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("rig --help", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ported_command_is_parsed_here_not_forwarded()
    {
        Assert.Equal(0, await Run("gh", "threads", "--help"));

        Assert.Empty(_proc.Requests);
        Assert.Contains("resolve", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_python_command_sent_from_python_is_refused_not_sent_back()
    {
        _fromPython = true;

        Assert.Equal(2, await Run("sync"));

        Assert.Empty(_proc.Requests);
        Assert.Contains("refusing to loop", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("'sync'", _err.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
    }
}
