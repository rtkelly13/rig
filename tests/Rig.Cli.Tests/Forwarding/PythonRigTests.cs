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
    public void A_module_runs_in_the_checkouts_uv_environment()
    {
        Assert.Equal(
            ["uv", "run", "--no-sync", "--project", "/rig", "python", "-m", "desktop_manager.web_upstream", "--port", "1"],
            new PythonRig("/rig").Module("desktop_manager.web_upstream", ["--port", "1"]));
    }
}
