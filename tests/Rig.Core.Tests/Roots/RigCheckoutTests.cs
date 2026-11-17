using Rig.Core.Roots;

namespace Rig.Core.Tests.Roots;

public sealed class RigCheckoutTests
{
    [Fact]
    public void The_checkout_defaults_to_personal_rig_under_the_code_root()
    {
        var checkout = RigCheckout.Resolve(null, Path.Combine("home", "code"));

        Assert.Equal(Path.Combine("home", "code", "personal", "rig"), checkout);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_rig_home_is_ignored(string? configured)
    {
        Assert.EndsWith("rig", RigCheckout.Resolve(configured, "root"), StringComparison.Ordinal);
    }

    [Fact]
    public void Rig_home_overrides_the_checkout()
    {
        Assert.Equal("/elsewhere/rig", RigCheckout.Resolve("/elsewhere/rig", "root"));
    }
}
