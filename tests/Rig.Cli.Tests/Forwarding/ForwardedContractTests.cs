using System.Text.RegularExpressions;
using Rig.Cli.Agent;
using Rig.Cli.Browser;
using Rig.Cli.Devenv;
using Rig.Cli.Forwarding;
using Rig.Cli.Latex;
using Rig.Cli.Mail;
using Rig.Cli.Models;
using Rig.Cli.Otel;
using Rig.Cli.Policy;
using Rig.Cli.Skills;
using Rig.Cli.Status;
using Rig.Cli.Vault;
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
/// The forwarding table is copied from the Python CLI, so it is checked against it: a command added to or ported out
/// of <c>src/rig/rig_cli.py</c> without the table following fails here, rather than vanishing from <c>rig</c>.
/// </summary>
public sealed partial class ForwardedContractTests
{
    private static readonly string PythonCli = Path.Combine(RepoRoot(), "src", "rig", "rig_cli.py");
    private static readonly string Bridge = Path.Combine(RepoRoot(), "src", "rig", "csharp_cli.py");
    private static readonly string Project = Path.Combine(RepoRoot(), "pyproject.toml");
    private static readonly HttpClient Refusing = new(new StubHandler(_ => throw new InvalidOperationException("nothing may connect")));

    [Fact]
    public void The_table_names_exactly_the_python_commands()
    {
        var source = File.ReadAllText(PythonCli);
        var python = SubApp().Matches(source).Concat(Command().Matches(source)).Select(m => m.Groups["name"].Value);

        Assert.Equal(python.Order(StringComparer.Ordinal), Forwarded.Commands.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_command_python_hands_over_is_one_csharp_owns()
    {
        // Python hands over the PORTED rig subcommands, and each entry point whose script is the bridge's entry().
        var block = PortedBlock().Match(File.ReadAllText(Bridge)).Groups["body"].Value;
        var ported = Name().Matches(block).Select(m => m.Groups["name"].Value)
            .Concat(HandedOverScript().Matches(File.ReadAllText(Project)).Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal) // agent and status are both rig subcommands and entry points
            .ToList();
        var forwarded = Forwarded.Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var owned = FullApp().BuildRoot().Subcommands.Select(c => c.Name).Where(name => !forwarded.Contains(name));

        Assert.NotEmpty(ported);
        Assert.Equal(ported.Order(StringComparer.Ordinal), owned.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_subcommand_python_hands_over_is_one_csharp_owns()
    {
        // PORTED_SUBCOMMANDS: the subcommands of a group Python still owns that C# runs (devenv claude).
        var block = PortedSubcommandsBlock().Match(File.ReadAllText(Bridge)).Groups["body"].Value;
        var python = Group().Matches(block)
            .SelectMany(m => Name().Matches(m.Groups["subs"].Value).Select(s => $"{m.Groups["group"].Value} {s.Groups["name"].Value}"));
        var forwarded = Forwarded.Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var csharp = FullApp().BuildRoot().Subcommands
            .Where(group => forwarded.Contains(group.Name))
            .SelectMany(group => group.Subcommands.Select(sub => $"{group.Name} {sub.Name}"));

        Assert.NotEmpty(python);
        Assert.Equal(python.Order(StringComparer.Ordinal), csharp.Order(StringComparer.Ordinal));
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
                new MailCommand(new MailHost(_ => null, "/", "/", TimeProvider.System), TextWriter.Null, TextWriter.Null).Build,
                InertOtelStack().Build,
                new StatusCommand(new FakeProc(), new Git(new FakeProc(), "/"), "/", TextWriter.Null).Build,
                () => new ModelsCommand(new ModelDiscovery(new FakeProc(), "/", _ => null), TextWriter.Null).Build(),
                new PolicyCommand(new PolicyHost("/none.json", "/", "/", false), TextWriter.Null, TextWriter.Null).Build,
                new SkillsCommand(new SkillsHost("/", "/", "/", "/", false), new Git(new FakeProc(), "/"), new RefusingLinker(), TextWriter.Null, TextWriter.Null).Build,
                new BrowserCommand(new BrowserHost("/", null, false, _ => null, _ => new ShellProfileEnvironment("/none")), new FakeProc(), TextWriter.Null, TextWriter.Null).Build,
                new DevenvCommand(new DevenvHost("/", "/none.json", false), new ObsidianPlugins(Refusing), TextWriter.Null, TextWriter.Null).Build,
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

    [Fact]
    public void The_shortcut_flags_match_the_python_ones()
    {
        var source = File.ReadAllText(PythonCli);
        var block = ShortcutBlock().Match(source).Groups["body"].Value;
        var python = Quoted().Matches(block).Select(m => m.Groups["flag"].Value);

        Assert.Equal(python.Order(StringComparer.Ordinal), AgentLauncher.ShortcutFlags.Order(StringComparer.Ordinal));
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

    [GeneratedRegex("""add_typer\(\w+, name="(?<name>[\w-]+)"\)""")]
    private static partial Regex SubApp();

    [GeneratedRegex(@"@app\.command\(""(?<name>[\w-]+)""")]
    private static partial Regex Command();

    [GeneratedRegex("""AGENT_SHORTCUT_FLAGS = \{(?<body>[^}]*)\}""")]
    private static partial Regex ShortcutBlock();

    [GeneratedRegex("""PORTED: tuple\[str, \.\.\.\] = \((?<body>[^)]*)\)""")]
    private static partial Regex PortedBlock();

    [GeneratedRegex("""PORTED_SUBCOMMANDS: dict\[str, tuple\[str, \.\.\.\]\] = \{(?<body>[^}]*)\}""")]
    private static partial Regex PortedSubcommandsBlock();

    [GeneratedRegex(@"""(?<group>[\w-]+)"": \((?<subs>[^)]*)\)")]
    private static partial Regex Group();

    [GeneratedRegex(@"""(?<name>[\w-]+)""")]
    private static partial Regex Name();

    [GeneratedRegex(@"^(?<name>[\w-]+) = ""rig\.csharp_cli:entry""\r?$", RegexOptions.Multiline)]
    private static partial Regex HandedOverScript();

    [GeneratedRegex(@"""(?<flag>-[\w-]+)""")]
    private static partial Regex Quoted();
}
