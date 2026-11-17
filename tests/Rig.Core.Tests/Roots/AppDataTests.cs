using Rig.Core.Roots;

namespace Rig.Core.Tests.Roots;

/// <summary><see cref="AppData"/> beyond the cases the knowledge-server oracle recorded.</summary>
public sealed class AppDataTests
{
    [Fact]
    public void The_current_folder_is_named_for_rig()
    {
        Assert.Equal(AppData.Name, Path.GetFileName(AppData.Current()));
    }
}
