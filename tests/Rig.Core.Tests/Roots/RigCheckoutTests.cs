using Rig.Core.Roots;

namespace Rig.Core.Tests.Roots;

public sealed class RigCheckoutTests
{
    [Fact]
    public void The_estate_clones_rig_into_personal()
    {
        Assert.Equal(Path.Combine("home", "code", "personal", "rig"), RigCheckout.Under(Path.Combine("home", "code")));
    }

    [Fact]
    public void A_build_output_belongs_to_the_checkout_above_it()
    {
        using var dir = new TempDir();
        var checkout = dir.Folder("rig");
        dir.File("rig/rig.slnx");
        dir.File("rig/src/Rig.Cli/Rig.Cli.csproj");

        Assert.Equal(Path.GetFullPath(checkout), RigCheckout.FromBuildOutput(dir.Folder("rig/src/Rig.Cli/bin/Debug/net11.0")));
        Assert.Null(RigCheckout.FromBuildOutput(dir.Folder("elsewhere")));
    }
}
