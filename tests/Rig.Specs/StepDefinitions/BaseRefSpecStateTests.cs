namespace Rig.Specs.StepDefinitions;

/// <summary>
/// The feature words and the enums they map to. These guard the failure message a future example sees when it names a
/// state or status nobody arranged, so the fix is obvious instead of a missing step binding.
/// </summary>
public sealed class BaseRefSpecStateTests
{
    [Theory]
    [InlineData("level", BaseRefState.Level)]
    [InlineData("behind 3", BaseRefState.Behind3)]
    [InlineData("ahead 2", BaseRefState.Ahead2)]
    [InlineData("other upstream", BaseRefState.OtherUpstream)]
    [InlineData("missing branch", BaseRefState.MissingBranch)]
    [InlineData("missing remote", BaseRefState.MissingRemote)]
    public void Every_state_the_feature_names_parses(string label, BaseRefState expected) =>
        Assert.Equal(expected, BaseRefSpecState.ParseState(label));

    [Theory]
    [InlineData("current", BaseRefStatus.Current)]
    [InlineData("behind", BaseRefStatus.Behind)]
    [InlineData("ahead", BaseRefStatus.Ahead)]
    [InlineData("absent", BaseRefStatus.Absent)]
    public void Every_status_the_feature_names_parses(string label, BaseRefStatus expected) =>
        Assert.Equal(expected, BaseRefSpecState.ParseStatus(label));

    [Fact]
    public void Every_state_enum_member_has_a_feature_word() =>
        Assert.Equal(
            Enum.GetValues<BaseRefState>().Length,
            BaseRefSpecState.KnownStates.Select(BaseRefSpecState.ParseState).Distinct().Count());

    [Fact]
    public void Every_status_enum_member_has_a_feature_word() =>
        Assert.Equal(
            Enum.GetValues<BaseRefStatus>().Length,
            BaseRefSpecState.KnownStatuses.Select(BaseRefSpecState.ParseStatus).Distinct().Count());

    [Fact]
    public void An_unknown_state_says_what_is_known_and_where_to_add_it()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BaseRefSpecState.ParseState("sideways"));

        Assert.Contains("\"sideways\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("Known states: ahead 2, behind 3, level, missing branch, missing remote, other upstream", error.Message, StringComparison.Ordinal);
        Assert.Contains("BaseRefState", error.Message, StringComparison.Ordinal);
        Assert.Contains("OrcaBaseRefWorld", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_status_says_what_is_known_and_where_to_assert_it()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BaseRefSpecState.ParseStatus("sideways"));

        Assert.Contains("\"sideways\"", error.Message, StringComparison.Ordinal);
        Assert.Contains("Known statuses: absent, ahead, behind, current", error.Message, StringComparison.Ordinal);
        Assert.Contains("BaseRefStatus", error.Message, StringComparison.Ordinal);
        Assert.Contains("ThenTheLocalBaseIs", error.Message, StringComparison.Ordinal);
    }
}
