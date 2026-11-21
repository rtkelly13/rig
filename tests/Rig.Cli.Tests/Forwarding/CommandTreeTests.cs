using System.CommandLine;
using System.Runtime.InteropServices;
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
        Assert.False(inspected[0].Action);
        Assert.All(inspected.Skip(1), c =>
        {
            Assert.True(c.Action);
            Assert.Single(c.Aliases);
        });
        using var json = JsonDocument.Parse(CommandTree.Export(root, OSPlatform.Linux));
        Assert.Equal(0, calls);
        Assert.Equal(3, json.RootElement.GetProperty("commands").GetArrayLength());
        Assert.NotEmpty(json.RootElement.GetProperty("jobs").EnumerateArray());
        Assert.Contains(json.RootElement.GetProperty("rawAliases").EnumerateArray(), alias => alias.GetProperty("Path").GetString() == "agent models");
        Assert.NotEmpty(json.RootElement.GetProperty("agentTargets").EnumerateArray());
    }
    [Fact]
    public void Inspection_can_target_windows_mac_and_linux_without_changing_the_host()
    {
        var root = new RootCommand();
        string[] Jobs(OSPlatform platform)
        {
            using var json = JsonDocument.Parse(CommandTree.Export(root, platform));
            return json.RootElement.GetProperty("jobs").EnumerateArray().Select(x => x.GetString()!).ToArray();
        }

        var windows = Jobs(OSPlatform.Windows);
        var mac = Jobs(OSPlatform.OSX);
        var linux = Jobs(OSPlatform.Linux);

        Assert.Contains(windows, x => x.StartsWith("restore=/scripts/rig.exe", StringComparison.Ordinal));
        Assert.DoesNotContain(windows, x => x.StartsWith("web=", StringComparison.Ordinal));
        Assert.Contains(mac, x => x == "web=/scripts/rig tools ensure web");
        Assert.DoesNotContain(mac, x => x.StartsWith("restore=", StringComparison.Ordinal));
        Assert.DoesNotContain(linux, x => x.StartsWith("web=", StringComparison.Ordinal));
        Assert.Contains(linux, x => x.StartsWith("estate-sync=/scripts/rig estate", StringComparison.Ordinal));
    }

}
