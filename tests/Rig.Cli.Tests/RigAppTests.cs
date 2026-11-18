using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using Rig.Cli.Agent;
using Rig.Core;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests;

public sealed class RigAppTests : IDisposable
{
    private readonly FakeProc _proc = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    [SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "RigApp takes command factories and this test injects a throwaway System.CommandLine group to check overview text; a DotMake class would have to be a named production type.")]
    private Task<int> Run(params string[] args) =>
        new RigApp(
            new FakeGitHub(),
            _ => Task.FromResult<RepoSlug?>(null),
            _out,
            _err,
            [
                () => new Rig.Cli.Models.ModelsCommand(new Rig.Cli.Models.ModelDiscovery(_proc, "/home", _ => null), _out).Build(),
                () => new System.CommandLine.Command("chrome-tabs", "Chrome tabs.") { new System.CommandLine.Command("doctor", "Chrome tabs doctor here.") },
            ],
            new AgentLauncher(_proc, new AgentHost("/home/none.json", "/work", p => $"/bin/{p}"), _out, _err))
            .RunAsync(args, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Help_prints_the_overview_and_names_no_other_cli()
    {
        Assert.Equal(0, await Run("help"));

        Assert.Empty(_proc.Requests);
        var help = _out.ToString();
        Assert.Contains(RigApp.Description, help, StringComparison.Ordinal);
        Assert.Contains("  chrome-tabs ", help, StringComparison.Ordinal);
        Assert.DoesNotContain("desktop-manager", help, StringComparison.Ordinal);
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
    public async Task A_subcommand_of_a_group_is_parsed_here()
    {
        Assert.Equal(0, await Run("chrome-tabs", "doctor", "--help"));

        Assert.Empty(_proc.Requests);
        Assert.Contains("Chrome tabs doctor here.", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_records_a_root_span_with_the_command_line_and_working_directory()
    {
        using var parent = new Activity("test");
        _ = parent.SetIdFormat(ActivityIdFormat.W3C).Start();
        var spans = new List<Activity>();
        using var listener = Listen(spans, parent);

        Assert.Equal(0, await Run("--version"));

        var span = Assert.Single(spans);
        Assert.Equal("rig --version", span.OperationName);
        Assert.Equal<string>(["--version"], (string[])span.GetTagItem("cli.argv")!);
        Assert.Equal(Environment.CurrentDirectory, span.GetTagItem("cli.cwd"));
        Assert.Equal(Environment.CurrentDirectory, span.GetTagItem("process.cwd"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task A_failing_command_marks_its_root_span_as_an_error()
    {
        using var parent = new Activity("test");
        _ = parent.SetIdFormat(ActivityIdFormat.W3C).Start();
        var spans = new List<Activity>();
        using var listener = Listen(spans, parent);

        Assert.Equal((int)ExitCode.Error, await Run("no-such-command"));

        var span = Assert.Single(spans);
        Assert.Equal("rig no-such-command", span.OperationName);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal((int)ExitCode.Error, span.GetTagItem("process.exit.code"));
    }

    public static TheoryData<string[]> AgentLaunches()
    {
        var launches = new TheoryData<string[]>();
        launches.Add(Array.Empty<string>());
        launches.Add(["-ccc", "--resume"]);
        return launches;
    }

    [Theory]
    [MemberData(nameof(AgentLaunches))]
    public async Task An_agent_launch_names_its_root_span_rig_agent(string[] args)
    {
        using var parent = new Activity("test");
        _ = parent.SetIdFormat(ActivityIdFormat.W3C).Start();
        var spans = new List<Activity>();
        using var listener = Listen(spans, parent);
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "/bin/claude");

        Assert.Equal(0, await Run(args));

        var span = Assert.Single(spans);
        Assert.Equal("rig agent", span.OperationName);
        Assert.Equal(args, (string[])span.GetTagItem("cli.argv")!);
    }

    // Only this test's spans: other test classes run RigApp concurrently on the same source.
    private static ActivityListener Listen(List<Activity> spans, Activity parent)
    {
        // Create the source before the listener compares identity: ActivitySource calls ShouldListenTo while its own
        // static field is still being assigned, where RigApp.Source would read null.
        _ = RigApp.Source;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source == RigApp.Source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == parent.TraceId)
                {
                    spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
    }
}
