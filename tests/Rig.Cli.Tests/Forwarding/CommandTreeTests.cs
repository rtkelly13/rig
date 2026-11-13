using System.CommandLine;
using System.Text.Json;
using Rig.Cli.Forwarding;

namespace Rig.Cli.Tests.Forwarding;

public sealed class CommandTreeTests
{
    [Fact]
    public void Dynamic_registrations_aliases_and_actions_come_from_the_composed_tree()
    {
        var group = new Command("chrome-tabs");
        var calls = 0;
        foreach (var verb in new[] { "one", "two" })
        {
            var child = new Command(verb);
            child.Aliases.Add(verb + "-alias");
            child.SetAction(_ => calls++);
            group.Subcommands.Add(child);
        }

        var root = new RootCommand { group };
        var inspected = CommandTree.Read(root);
        Assert.Equal(["chrome-tabs", "chrome-tabs one", "chrome-tabs two"], inspected.Select(c => c.Path));
        Assert.True(inspected[0].Forwarded);
        Assert.All(inspected.Skip(1), c =>
        {
            Assert.True(c.Action);
            Assert.False(c.Forwarded);
            Assert.Single(c.Aliases);
        });
        using var json = JsonDocument.Parse(CommandTree.Export(root, [["chrome-tabs", "one"], ["chrome-tabs", "organise"]], false));
        var dispatch = json.RootElement.GetProperty("dispatch").EnumerateArray().ToArray();
        Assert.False(dispatch[0].GetProperty("Forwarded").GetBoolean());
        Assert.True(dispatch[1].GetProperty("Forwarded").GetBoolean());
        Assert.Equal(0, calls);
        Assert.NotEmpty(json.RootElement.GetProperty("jobs").EnumerateArray());
        Assert.NotEmpty(json.RootElement.GetProperty("forwarding").EnumerateArray());
        Assert.Contains(json.RootElement.GetProperty("rawAliases").EnumerateArray(), alias => alias.GetProperty("Path").GetString() == "agent models");
        Assert.NotEmpty(json.RootElement.GetProperty("agentTargets").EnumerateArray());
    }
}
