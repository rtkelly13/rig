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
    [InlineData(new[] { "web" }, false)]
    [InlineData(new[] { "web", "--port", "8095", "--no-open" }, false)]
    [InlineData(new[] { "web", "-p", "8095" }, false)]
    [InlineData(new[] { "web", "--help" }, true)]
    [InlineData(new[] { "web", "-p", "8095", "-h" }, true)]
    [InlineData(new[] { "web", "install" }, true)]
    [InlineData(new[] { "web", "mcp" }, true)]
    [InlineData(new[] { "web", "--port", "8095" }, false)]
    [InlineData(new[] { "web", "--host", "0.0.0.0", "--preview" }, false)]
    [InlineData(new[] { "web", "--no-open", "backup" }, true)]
    [InlineData(new[] { "web", "--port", "8095", "install" }, true)]
    public void A_bare_web_line_runs_on_csharp_and_its_subcommands_and_help_on_python(string[] args, bool python)
    {
        var root = new RootCommand { new Command("web") };

        Assert.Equal(python, Forwarded.Owns(args, root));
    }

    [Fact]
    public void A_bare_line_stays_in_python_until_csharp_registers_the_group()
    {
        Assert.True(Forwarded.Owns(["web"], Empty));
    }
}
