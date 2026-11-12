using System.Text.RegularExpressions;
using Rig.Cli.Agent;
using Rig.Cli.Browser;
using Rig.Cli.Chrome;
using Rig.Cli.Devenv;
using Rig.Cli.Estate;
using Rig.Cli.Forwarding;
using Rig.Cli.Governance;
using Rig.Cli.Latex;
using Rig.Cli.Mail;
using Rig.Cli.Models;
using Rig.Cli.Otel;
using Rig.Cli.Policy;
using Rig.Cli.Skills;
using Rig.Cli.Status;
using Rig.Cli.Tests.Estate;
using Rig.Cli.Vault;
using Rig.Cli.Web;
using Rig.Cli.Workspace;
using Rig.Core.Browser;
using Rig.Core.Devenv;
using Rig.Core.GitHub;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;
using Rig.Core.Tests.Skills;

namespace Rig.Cli.Tests.Forwarding;

/// <summary>
/// The rig scripts and the forwarding table, checked against <c>pyproject.toml</c>: every script but
/// <c>desktop-manager</c> starts this CLI, each names a command it owns, and nothing forwarded goes back to <c>rig</c>.
/// Each forwarded Python program is checked to answer <c>--help</c> by <c>tests/test_csharp_cli.py</c>.
/// </summary>
public sealed partial class ForwardedContractTests
{
    private static readonly string Project = Path.Combine(RepoRoot(), "pyproject.toml");
    private static readonly HttpClient Refusing = new(new StubHandler(_ => throw new InvalidOperationException("nothing may connect")));

    [Fact]
    public void Every_script_but_desktop_manager_starts_the_csharp_cli()
    {
        var scripts = Scripts();

        Assert.Contains("rig", scripts.Keys);
        Assert.All(
            scripts.Where(s => s.Key != "desktop-manager"),
            s => Assert.True(s.Value == "rig.csharp_cli:entry", $"{s.Key} = {s.Value}, not the C# CLI"));
    }

    [Fact]
    public void Every_script_names_a_command_csharp_owns()
    {
        // `uv run latex build` runs `rig latex build`, so latex must be a C# command, not a forwarded one.
        var forwarded = Forwarded.Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var owned = FullApp().BuildRoot().Subcommands.Select(c => c.Name).Where(name => !forwarded.Contains(name)).ToHashSet(StringComparer.Ordinal);
        var handedOver = Scripts().Where(s => s.Value == "rig.csharp_cli:entry" && s.Key != "rig").Select(s => s.Key).ToList();

        Assert.NotEmpty(handedOver);
        Assert.All(handedOver, name => Assert.Contains(name, owned));
    }

    [Fact]
    public void Nothing_forwarded_goes_back_to_rig()
    {
        // The rig script is this CLI: a row launching it would loop.
        Assert.All(Forwarded.Commands, c => Assert.NotEqual("rig", c.Launch[0]));
        Assert.All(Forwarded.Commands, c => Assert.True(c.Launch[0] is "python" or "desktop-manager", $"{c.Name} launches {c.Launch[0]}"));
    }

    // Every ported command, built with fakes; only the command tree is read.
    private static RigApp FullApp() =>
        new(
            new PythonRig(new FakeProc(), "/rig"),
            new FakeGitHub(),
            _ => Task.FromResult<RepoSlug?>(null),
            TextWriter.Null,
            TextWriter.Null,
            [
                new LatexCommand(new FakeProc(), new LatexHost(_ => null, string.Empty, "/", "/"), TextWriter.Null, TextWriter.Null).Build,
                new MdLintCommand(new FakeProc(), new MdLintHost("/rig", _ => null, "/"), TextWriter.Null).Build,
                new VaultCommand(
                    new FakeProc(),
                    new VaultHost(_ => null, "/"),
                    new KnowledgeServerCommand(new FakeProc(), _ => throw new InvalidOperationException(), false, _ => null, TextWriter.Null, TextWriter.Null),
                    TextWriter.Null,
                    TextWriter.Null).Build,
                new ChromeTabsCommand(_ => null, "/", TimeProvider.System, TimeZoneInfo.Utc, TextWriter.Null).Build,
                new MailCommand(new MailHost(_ => null, "/", "/", TimeProvider.System), TextWriter.Null, TextWriter.Null).Build,
                InertOtelStack().Build,
                new StatusCommand(new FakeProc(), new Git(new FakeProc(), "/"), "/", TextWriter.Null).Build,
                () => new ModelsCommand(new ModelDiscovery(new FakeProc(), "/", _ => null), TextWriter.Null).Build(),
                new PolicyCommand(new PolicyHost("/none.json", "/", "/", false), TextWriter.Null, TextWriter.Null).Build,
                new SkillsCommand(new SkillsHost("/", "/", "/", "/", false), new Git(new FakeProc(), "/"), new RefusingLinker(), TextWriter.Null, TextWriter.Null).Build,
                new BrowserCommand(new BrowserHost("/", null, false, _ => null, _ => new ShellProfileEnvironment("/none")), new FakeProc(), TextWriter.Null, TextWriter.Null).Build,
                new DevenvCommand(new DevenvHost("/", "/none.json", false, "/", "/", null, _ => null), new ObsidianPlugins(Refusing), new NerdFonts(Refusing), new FakeProc(), TextWriter.Null, TextWriter.Null).Build,
                new DoctorCommand(new DevenvHost("/", "/none.json", false, "/", "/", null, _ => null), new FakeProc(), TimeProvider.System, TextWriter.Null).Build,
                new WorkspaceCommand(new WorkspaceHost("/", "/none.json", "/", "/none.json", "/none.json"), new FakeProc(), TimeProvider.System, TextWriter.Null, Refusing).Build,
                new WorkspaceCommand(new WorkspaceHost("/", "/none.json", "/", "/none.json", "/none.json"), new FakeProc(), TimeProvider.System, TextWriter.Null, Refusing).BuildSync,
                new EstateCommand(new EstateHost("/", "/", "/"), NoOrca.Host, new FakeProc(), new FakeGitHub(), TimeProvider.System, _ => null, TextWriter.Null, TextWriter.Null).Build,
                new GovernanceCommand(new GovernanceHost("/", "/", "/"), new FakeProc(), TimeProvider.System, TextWriter.Null).Build,
                new WebCommand(
                    new WebPlaces("/rig", "/none.json", "/", "/rig.db"),
                    _ => throw new InvalidOperationException("nothing may start"),
                    new FakeProbe(),
                    _ => throw new InvalidOperationException("nothing may open"),
                    (_, _) => Task.CompletedTask,
                    TextWriter.Null,
                    TextWriter.Null).Build,
            ],
            new AgentLauncher(new FakeProc(), new AgentHost("/none.json", "/", _ => null), TextWriter.Null, TextWriter.Null));

    // Only its command tree is read; nothing here runs.
    private static OtelStackCommand InertOtelStack()
    {
        var paths = new OtelPaths("/otel");
        var signals = new FakeSignals();
        var detached = new RecordingDetached(signals);
        var probe = new FakeProbe();
        var runtime = new CollectorRuntime(paths, new FakeProc(), detached, windows: false);
        return new OtelStackCommand(
            new OtelHost(paths, () => "darwin_arm64", true, "/traces", _ => null, TimeProvider.System, TimeZoneInfo.Utc),
            new OtelParts(
                runtime,
                probe,
                new OtelInstaller(Refusing),
                _ => throw new InvalidOperationException("nothing may run"),
                new ClickHousePair(paths, signals, detached, probe, runtime, TimeProvider.System, (_, _) => Task.CompletedTask),
                new ClickHouseClient(Refusing, new Uri("http://clickhouse.test:1")),
                new FakeSender(),
                (_, _) => Task.CompletedTask),
            TextWriter.Null,
            TextWriter.Null);
    }

    private static Dictionary<string, string> Scripts()
    {
        var source = File.ReadAllText(Project);
        var block = ScriptsBlock().Match(source).Groups["body"].Value;
        return Script().Matches(block).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["target"].Value, StringComparer.Ordinal);
    }

    [Fact]
    public void Every_group_run_bare_on_csharp_is_forwarded_and_registered()
    {
        // A bare `rig web --port 8095` runs on C#, so C# registers `web`; its subcommands still go to Python.
        var registered = FullApp().BuildRoot().Subcommands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var forwarded = Forwarded.Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(Forwarded.RunsBare);
        Assert.All(Forwarded.RunsBare, name => Assert.Contains(name, registered));
        Assert.All(Forwarded.RunsBare, name => Assert.Contains(name, forwarded));
    }

    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "rig.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("rig.slnx is not above the test output folder.");
    }

    [GeneratedRegex(@"^\[project\.scripts\]\r?\n(?<body>(?:[^\[\r\n].*\r?\n)*)", RegexOptions.Multiline)]
    private static partial Regex ScriptsBlock();

    [GeneratedRegex(@"^(?<name>[\w-]+) = ""(?<target>[^""]+)""\r?$", RegexOptions.Multiline)]
    private static partial Regex Script();
}
