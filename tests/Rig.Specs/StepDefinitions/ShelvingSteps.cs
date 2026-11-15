using Reqnroll;
using Rig.Core.Estate;

namespace Rig.Specs.StepDefinitions;

/// <summary>Binds the shelving feature to <see cref="ShelfFixture"/>, in memory by default and on disk for @real.</summary>
[Binding]
public sealed class ShelvingSteps(ScenarioContext scenario) : IDisposable
{
    private readonly ShelfFixture _fixture = new();
    private string _name = string.Empty;
    private ShelfTrailers _trailers = new();

    /// <summary>A worktree with work only it holds.</summary>
    [Given(@"^a worktree with a tracked edit and an untracked scratch file$")]
    public async Task GivenAWorktreeWithUncommittedWork() => await _fixture.BuildAsync(SpecMode.Real(scenario));

    /// <summary>The same worktree, built on disk even without the mode switch.</summary>
    [Given(@"^a real worktree with a tracked edit and an untracked scratch file$")]
    public async Task GivenARealWorktreeWithUncommittedWork() => await _fixture.BuildAsync(real: true);

    /// <summary>An ignored folder a command regenerates.</summary>
    [Given(@"^an ignored build output that regenerates$")]
    public void GivenAnIgnoredBuildOutputThatRegenerates() => _fixture.AddRegenerableBuildOutput();

    /// <summary>Runs the shelve.</summary>
    [When(@"^the worktree is shelved$")]
    public async Task WhenTheWorktreeIsShelved() => await _fixture.ShelveAsync();

    /// <summary>The worktree is out of the way.</summary>
    [Then(@"^the worktree is gone$")]
    public void ThenTheWorktreeIsGone() => _fixture.AssertGone();

    /// <summary>Origin holds the snapshot, so the removal proved itself.</summary>
    [Then(@"^the shelf holds its work on origin$")]
    public void ThenTheShelfHoldsItsWorkOnOrigin() => _fixture.AssertShelfHoldsWork();

    /// <summary>The ignored folder is one a command may rebuild.</summary>
    [Then(@"^the ignored build output can be regenerated$")]
    public void ThenTheIgnoredBuildOutputCanBeRegenerated() => _fixture.AssertBuildOutputRegenerates();

    /// <summary>Brings the shelf back.</summary>
    [When(@"^the shelf is restored$")]
    public async Task WhenTheShelfIsRestored() => await _fixture.RestoreAsync();

    /// <summary>The tracked edit is restored byte for byte.</summary>
    [Then(@"^the tracked edit is back$")]
    public async Task ThenTheTrackedEditIsBack() => await _fixture.AssertTrackedEditBack();

    /// <summary>The untracked file is restored.</summary>
    [Then(@"^the untracked scratch file is back$")]
    public void ThenTheUntrackedScratchFileIsBack() => _fixture.AssertUntrackedBack();

    /// <summary>Regenerable output is not what a shelf exists to carry.</summary>
    [Then(@"^the ignored build output was not carried$")]
    public void ThenTheIgnoredBuildOutputWasNotCarried() => _fixture.AssertIgnoredNotCarried();

    /// <summary>Derives the shelf name from a path inside the clone.</summary>
    [When(@"^a shelf is named for ""(.*)"" in the clone$")]
    public void WhenAShelfIsNamedFor(string relative)
    {
        var clone = Path.Join(Path.GetTempPath(), "rig-specs-clone");
        _name = Shelver.ShelfName(clone, Path.Join(clone, relative));
    }

    /// <summary>The ref origin stores the shelf under.</summary>
    [Then(@"^its ref is ""(.*)""$")]
    public void ThenItsRefIs(string expected) => Assert.Equal(expected, Shelver.ShelfRef("t", _name));

    /// <summary>Writes the marker commit message, the only thing unshelve reads besides the tree.</summary>
    [When(@"^a shelf marker is written for branch ""(.*)"" at ""(.*)"" on base ""(.*)""$")]
    public void WhenAShelfMarkerIsWritten(string branch, string path, string baseSha) =>
        _trailers = ShelfStore.ParseTrailers(Shelver.Message("clone", "spike", branch, baseSha, path, null, DateTimeOffset.UnixEpoch));

    /// <summary>The trailers name everything a restore needs.</summary>
    [Then(@"^the marker names branch ""(.*)"", path ""(.*)"" and base ""(.*)""$")]
    public void ThenTheMarkerNames(string branch, string path, string baseSha)
    {
        Assert.Equal(branch, _trailers.Branch);
        Assert.Equal(path, _trailers.Path);
        Assert.Equal(baseSha, _trailers.Base);
    }

    /// <inheritdoc />
    [AfterScenario]
    public void Dispose() => _fixture.Dispose();
}
