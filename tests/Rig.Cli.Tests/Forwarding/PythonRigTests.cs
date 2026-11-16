using System.CommandLine;
using Rig.Cli.Forwarding;

namespace Rig.Cli.Tests.Forwarding;

public sealed class PythonRigTests
{
    [Fact]
    public void The_checkout_defaults_to_personal_rig_under_the_code_root()
    {
        var checkout = PythonRig.ResolveCheckout(null, Path.Combine("home", "code"));

        Assert.Equal(Path.Combine("home", "code", "personal", "rig"), checkout);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_rig_home_is_ignored(string? configured)
    {
        Assert.EndsWith("rig", PythonRig.ResolveCheckout(configured, "root"), StringComparison.Ordinal);
    }

    [Fact]
    public void Rig_home_overrides_the_checkout()
    {
        Assert.Equal("/elsewhere/rig", PythonRig.ResolveCheckout("/elsewhere/rig", "root"));
    }

    private static readonly RootCommand Empty = new();

    [Fact]
    public void Every_python_command_is_owned_and_nothing_else()
    {
        Assert.All(Forwarded.Commands, c => Assert.True(Forwarded.Owns([c.Name, "--help"], Empty)));
        Assert.False(Forwarded.Owns(["--help"], Empty));
        Assert.False(Forwarded.Owns(["--version"], Empty));
        Assert.False(Forwarded.Owns([], Empty));
        Assert.False(Forwarded.Owns(["-ccc"], Empty));
    }

    [Theory]
    [InlineData(new[] { "chrome-tabs" }, true)]
    [InlineData(new[] { "chrome-tabs", "--help" }, true)]
    [InlineData(new[] { "chrome-tabs", "list" }, true)]
    [InlineData(new[] { "chrome-tabs", "--port", "8095" }, true)]
    public void A_python_group_is_owned_whole(string[] args, bool python)
    {
        var root = new RootCommand { new Command("chrome-tabs") };

        Assert.Equal(python, Forwarded.Owns(args, root));
    }

    [Fact]
    public void A_ported_subcommand_stays_on_csharp()
    {
        var root = new RootCommand { new Command("chrome-tabs") { new Command("organise") } };

        Assert.False(Forwarded.Owns(["chrome-tabs", "organise"], root));
        Assert.True(Forwarded.Owns(["chrome-tabs", "traces"], root));
    }
}
