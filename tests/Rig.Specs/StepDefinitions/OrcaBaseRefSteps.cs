using Reqnroll;
using Rig.Core.Orca;
using Rig.Core.Packages;

namespace Rig.Specs.StepDefinitions;

/// <summary>Binds the Orca base-ref feature to <see cref="OrcaBaseRefWorld"/> in whichever mode the scenario selects.</summary>
[Binding]
public sealed class OrcaBaseRefSteps(ScenarioContext scenario) : IDisposable
{
    private readonly OrcaBaseRefWorld _world = new();

    /// <summary>Builds one repo with scripted git output, or on disk when the mode or the tag says so.</summary>
    [Given(@"^an Orca repo whose local branch is (.+)$")]
    public async Task GivenAnOrcaRepoWhoseLocalBranchIs(string state) => await _world.BuildAsync(state, SpecMode.Real(scenario));

    /// <summary>Builds the repo on disk even without <c>@real</c>, so the executor is explicit in the feature.</summary>
    [Given(@"^a real Orca repo whose local branch is (.+)$")]
    public async Task GivenARealOrcaRepoWhoseLocalBranchIs(string state) => await _world.BuildAsync(state, real: true);

    /// <summary>Builds a set of real repos, all level with origin/main.</summary>
    [Given(@"^(\d+) real Orca repos that are current$")]
    public async Task GivenRealOrcaReposThatAreCurrent(int count) => await _world.BuildManyAsync(count);

    /// <summary>Runs the production read and the doctor's finding.</summary>
    [When(@"^the doctor reads the local base$")]
    public async Task WhenTheDoctorReadsTheLocalBase() => await _world.ReadAsync();

    /// <summary>Reads every repo with a bounded fan-out.</summary>
    [When(@"^the doctor reads every local base$")]
    public async Task WhenTheDoctorReadsEveryLocalBase() => await _world.ReadAsync(maxConcurrency: 4);

    /// <summary>Asserts the finding's status, or that there is no finding to make.</summary>
    [Then(@"^the local base is (current|behind|ahead|absent)$")]
    public void ThenTheLocalBaseIs(string status)
    {
        var finding = _world.BaseFinding();
        if (status == "absent")
        {
            Assert.Null(finding);
            return;
        }

        Assert.NotNull(finding);
        var expected = status switch
        {
            "current" or "ahead" => CheckStatus.Ok,
            "behind" => CheckStatus.Warn,
            _ => throw new InvalidOperationException($"the feature names a base status the specs do not know: {status}"),
        };
        Assert.Equal(expected, finding.Status);
    }

    /// <summary>Asserts the finding's detail, or that there is no finding.</summary>
    [Then(@"^the local base detail is (.+)$")]
    public void ThenTheLocalBaseDetailIs(string detail)
    {
        var finding = _world.BaseFinding();
        if (detail == "none")
        {
            Assert.Null(finding);
            return;
        }

        Assert.NotNull(finding);
        Assert.Equal(detail, finding.Detail);
    }

    /// <summary>Asserts how many git calls the read made for the one repo.</summary>
    [Then(@"^the read makes (\d+) git calls$")]
    public void ThenTheReadMakesGitCalls(int calls) => Assert.Equal(calls, _world.Calls);

    /// <summary>Asserts every repo was read as level with origin/main.</summary>
    [Then(@"^every local base is current$")]
    public void ThenEveryLocalBaseIsCurrent()
    {
        Assert.NotEmpty(_world.RepoNames);
        Assert.All(_world.RepoNames, name =>
        {
            var finding = _world.BaseFinding(name);
            Assert.NotNull(finding);
            Assert.Equal(CheckStatus.Ok, finding.Status);
        });
    }

    /// <summary>The waterfall invariant: a repo costs one spawn, not one per ref or per attribute.</summary>
    [Then(@"^the read makes one git call per repo$")]
    public void ThenTheReadMakesOneGitCallPerRepo() => Assert.Equal(_world.RepoNames.Count, _world.Calls);

    /// <summary>A generous ceiling that still catches a read that degenerates into a chain per repo.</summary>
    [Then(@"^the read finishes within (\d+) seconds$")]
    public void ThenTheReadFinishesWithin(int seconds) =>
        Assert.True(
            _world.Duration < TimeSpan.FromSeconds(seconds),
            $"the read took {_world.Duration.TotalSeconds:F2} s for {_world.RepoNames.Count} repos, over the {seconds} s budget");

    /// <inheritdoc />
    [AfterScenario]
    public void Dispose() => _world.Dispose();
}
