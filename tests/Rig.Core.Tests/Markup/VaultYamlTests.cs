using Rig.Core.Markup;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Markup;

public sealed class VaultYamlTests
{
    [Fact]
    public void A_float_that_is_only_underscores_is_refused_with_its_position()
    {
        var refusal = Assert.Throws<VaultYamlException>(() => VaultYaml.Load("a: 1\nb: ._\n"));

        Assert.Equal("malformed number '._' at line 2, column 4", refusal.Message);
    }
}
