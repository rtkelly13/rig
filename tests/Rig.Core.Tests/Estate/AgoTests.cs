using Rig.Core.Estate;

namespace Rig.Core.Tests.Estate;

/// <summary><see cref="Ago"/>: the age the estate commands print.</summary>
public sealed class AgoTests
{
    [Theory]
    [InlineData(-1, "just now")]
    [InlineData(59, "59m ago")]
    [InlineData(60, "1h ago")]
    [InlineData((47 * 60) + 59, "47h ago")]
    [InlineData(48 * 60, "2d ago")]
    [InlineData(10 * 24 * 60, "10d ago")]
    public void Minutes_under_an_hour_hours_under_two_days_then_days(int minutes, string expected) =>
        Assert.Equal(expected, Ago.Format(minutes < 0 ? null : TimeSpan.FromMinutes(minutes)));
}
