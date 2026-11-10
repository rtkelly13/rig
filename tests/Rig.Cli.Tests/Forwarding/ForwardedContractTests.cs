using System.Text.RegularExpressions;
using Rig.Cli.Forwarding;

namespace Rig.Cli.Tests.Forwarding;

/// <summary>
/// The forwarding table is copied from the Python CLI, so it is checked against it: a command added to or ported out
/// of <c>src/rig/rig_cli.py</c> without the table following fails here, rather than vanishing from <c>rig</c>.
/// </summary>
public sealed partial class ForwardedContractTests
{
    private static readonly string PythonCli = Path.Combine(RepoRoot(), "src", "rig", "rig_cli.py");

    [Fact]
    public void The_table_names_exactly_the_python_commands()
    {
        var source = File.ReadAllText(PythonCli);
        var python = SubApp().Matches(source).Concat(Command().Matches(source)).Select(m => m.Groups["name"].Value);

        Assert.Equal(python.Order(StringComparer.Ordinal), Forwarded.Commands.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_shortcut_flags_match_the_python_ones()
    {
        var source = File.ReadAllText(PythonCli);
        var block = ShortcutBlock().Match(source).Groups["body"].Value;
        var python = Quoted().Matches(block).Select(m => m.Groups["flag"].Value);

        Assert.Equal(python.Order(StringComparer.Ordinal), Forwarded.AgentShortcutFlags.Order(StringComparer.Ordinal));
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

    [GeneratedRegex(@"""(?<flag>-[\w-]+)""")]
    private static partial Regex Quoted();
}
