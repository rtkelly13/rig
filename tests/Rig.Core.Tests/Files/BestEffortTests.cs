using Rig.Core.Files;

namespace Rig.Core.Tests.Files;

public sealed class BestEffortTests
{
    [Fact]
    public void A_completed_step_reports_it_completed() =>
        Assert.True(BestEffort.Io(() => { }));

    [Fact]
    public void A_filesystem_refusal_is_dropped_and_reported()
    {
        Assert.False(BestEffort.Io(() => throw new IOException("locked")));
        Assert.False(BestEffort.Io(() => throw new UnauthorizedAccessException("denied")));
    }

    [Fact]
    public void Any_other_failure_still_propagates() =>
        Assert.Throws<InvalidOperationException>(() => BestEffort.Io(() => throw new InvalidOperationException("bug")));
}
