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

    [Fact]
    public void Every_python_command_is_owned_and_nothing_else()
    {
        Assert.All(Forwarded.Commands, c => Assert.True(Forwarded.Owns([c.Name, "--help"])));
        Assert.False(Forwarded.Owns(["--help"]));
        Assert.False(Forwarded.Owns(["--version"]));
        Assert.False(Forwarded.Owns([]));
        Assert.False(Forwarded.Owns(["-ccc"]));
    }
}
