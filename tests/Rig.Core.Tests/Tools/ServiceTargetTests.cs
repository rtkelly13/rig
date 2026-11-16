using Rig.Core.Tools;

namespace Rig.Core.Tests.Tools;

/// <summary>The service catalogue's names, ports and rig arguments.</summary>
public sealed class ServiceTargetTests
{
    [Fact]
    public void Every_target_has_a_stable_name_and_display_name()
    {
        Assert.Equal(["web", "mcp", "desktop", "collector"], ServiceTarget.All.Select(t => t.Name));
        Assert.Equal(["Web", "Mcp", "Desktop", "Collector"], ServiceTarget.All.Select(t => t.DisplayName));
        Assert.True(ServiceTarget.Web.LongLived);
        Assert.True(ServiceTarget.Mcp.LongLived);
        Assert.False(ServiceTarget.Desktop.LongLived);
        Assert.False(ServiceTarget.Collector.LongLived);
    }

    [Theory]
    [InlineData("web", "web")]
    [InlineData("mcp", "mcp")]
    [InlineData("desktop", "desktop")]
    [InlineData("collector", "collector")]
    public void A_name_finds_its_target(string name, string expected)
    {
        Assert.Equal(expected, ServiceTarget.Find(name)?.Name);
    }

    [Fact]
    public void An_unknown_or_missing_name_finds_nothing()
    {
        Assert.Null(ServiceTarget.Find(null));
        Assert.Null(ServiceTarget.Find("nope"));
        Assert.Null(ServiceTarget.Find("WEB"));
    }

    [Fact]
    public void Ports_follow_the_preview_and_override_rules()
    {
        Assert.Equal(8090, ServiceTarget.Web.Port(preview: false));
        Assert.Equal(8091, ServiceTarget.Web.Port(preview: true));
        Assert.Equal(8123, ServiceTarget.Web.Port(preview: true, explicitPort: 8123));
        Assert.Null(ServiceTarget.Mcp.Port(preview: false));
        Assert.Null(ServiceTarget.Desktop.Port(preview: true));
    }

    [Fact]
    public void Web_arguments_pass_the_port_and_preview_flag()
    {
        Assert.Equal(["web", "--port", "8090", "--no-open"], ServiceTarget.Web.Arguments(8090, preview: false));
        Assert.Equal(["web", "--port", "8091", "--no-open", "--preview"], ServiceTarget.Web.Arguments(8091, preview: true));
        Assert.Equal(["web", "--no-open"], ServiceTarget.Web.Arguments(null, preview: false));
    }

    [Fact]
    public void The_other_targets_name_the_command_they_run()
    {
        Assert.Equal(["web", "mcp"], ServiceTarget.Mcp.Arguments(null, preview: false));
        Assert.Equal(["desktop", "workspace", "restore"], ServiceTarget.Desktop.Arguments(null, preview: false));
        Assert.Equal(["otel-stack", "collector", "ensure"], ServiceTarget.Collector.Arguments(null, preview: false));
    }

    [Fact]
    public void An_unknown_target_has_no_arguments()
    {
        var unknown = new ServiceTarget("unknown", "Unknown", null, null, false);

        Assert.Throws<InvalidOperationException>(() => unknown.Arguments(null, preview: false));
    }
}
