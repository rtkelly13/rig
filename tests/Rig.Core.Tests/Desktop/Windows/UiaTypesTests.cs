using Rig.Core.Desktop.Windows.Uia;

namespace Rig.Core.Tests.Desktop.Windows;

public sealed class UiaTypesTests
{
    [Fact]
    public void A_rect_holds_its_left_and_top_edges_but_not_its_right_and_bottom()
    {
        var rect = new UiaRect(10, 20, 30, 40);

        Assert.True(rect.Contains(10, 20));
        Assert.True(rect.Contains(29, 39));
        Assert.False(rect.Contains(30, 25));
        Assert.False(rect.Contains(15, 40));
        Assert.False(rect.Contains(9, 25));
        Assert.False(rect.IsEmpty);
    }

    [Theory]
    [InlineData(10, 20, 10, 40)]
    [InlineData(10, 20, 30, 20)]
    [InlineData(10, 20, 5, 40)]
    public void A_rect_without_area_is_empty(int left, int top, int right, int bottom) =>
        Assert.True(new UiaRect(left, top, right, bottom).IsEmpty);

    [Fact]
    public void An_empty_condition_matches_on_nothing_in_particular()
    {
        var condition = new UiaCondition();

        Assert.Null(condition.ControlType);
        Assert.Null(condition.ClassName);
        Assert.Null(condition.Name);
        Assert.Equal(new UiaCondition(UiaControlType.TabItem, "TabView"), condition with { ControlType = UiaControlType.TabItem, ClassName = "TabView" });
    }

    [Fact]
    public void The_exception_carries_its_cause()
    {
        var cause = new InvalidOperationException("stale");

        Assert.Same(cause, new UiaException("gone", cause).InnerException);
        Assert.Equal("gone", new UiaException("gone").Message);
        Assert.NotNull(new UiaException().Message);
    }
}
