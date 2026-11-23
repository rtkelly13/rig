using Rig.Core.Processes;

namespace Rig.Core.Tests.Processes;

public sealed class TrackTests
{
    [Theory]
    [InlineData("", "", null, null, false)]
    [InlineData("origin/x", "[gone]", null, null, true)]
    [InlineData("origin/x", "[ahead 2, behind 1]", 2, 1, false)]
    [InlineData("origin/x", "[ahead 3]", 3, 0, false)]
    [InlineData("origin/x", "[behind 4]", 0, 4, false)]
    [InlineData("origin/x", "", 0, 0, false)]
    public void Parse_reads_ahead_behind_gone_and_no_upstream(string upstream, string track, int? ahead, int? behind, bool gone) =>
        Assert.Equal(new Track(ahead, behind, gone), Track.Parse(upstream, track));
}
