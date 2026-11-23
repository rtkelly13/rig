using Reqnroll;
using Rig.Core;
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
    public async Task GivenAnOrcaRepoWhoseLocalBranchIs(string state) =>
        await _world.BuildAsync(BaseRefSpecState.ParseState(state), SpecMode.Real(scenario));

    /// <summary>Builds the repo on disk even without <c>@real</c>, so the executor is explicit in the feature.</summary>
    [Given(@"^a real Orca repo whose local branch is (.+)$")]
    public async Task GivenARealOrcaRepoWhoseLocalBranchIs(string state) =>
        await _world.BuildAsync(BaseRefSpecState.ParseState(state), real: true);

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
    [Then(@"^the local base is (.+)$")]
    public void ThenTheLocalBaseIs(string status)
    {
        var expected = BaseRefSpecState.ParseStatus(status);
        var finding = _world.BaseFinding();
        if (expected == BaseRefStatus.Absent)
        {
            Assert.True(
                finding is null,
                $"expected no local-base finding for {status}, but got: {_world.DescribeFindings()}");
            return;
        }

        Assert.True(
            finding is not null,
            $"expected a local-base finding for {status}, but the doctor reported none: {_world.DescribeFindings()}");
        var actual = expected switch
        {
            BaseRefStatus.Current or BaseRefStatus.Ahead => CheckStatus.Ok,
            BaseRefStatus.Behind => CheckStatus.Warn,
            _ => throw new InvalidOperationException(
                $"OrcaBaseRefSteps has no assertion for {expected}. Add one beside the other BaseRefStatus cases."),
        };
        Assert.True(
            finding.Status == actual,
            $"expected local base {status} ({actual}), but got {finding.Status}: {finding.Detail}. All findings: {_world.DescribeFindings()}");
    }

    /// <summary>Asserts the finding's detail, or that there is no finding.</summary>
    [Then(@"^the local base detail is (.+)$")]
    public void ThenTheLocalBaseDetailIs(string detail)
    {
        var finding = _world.BaseFinding();
        if (detail == "none")
        {
            Assert.True(
                finding is null,
                $"expected no local-base detail, but got: {finding?.Detail}. All findings: {_world.DescribeFindings()}");
            return;
        }

        Assert.True(
            finding is not null,
            $"expected local-base detail \"{detail}\", but the doctor reported no finding: {_world.DescribeFindings()}");
        Assert.True(
            finding.Detail == detail,
            $"expected local-base detail \"{detail}\", but got \"{finding.Detail}\". All findings: {_world.DescribeFindings()}");
    }

    /// <summary>Asserts how many git calls the read made for the one repo.</summary>
    [Then(@"^the read makes (\d+) git calls$")]
    public void ThenTheReadMakesGitCalls(int calls) =>
        Assert.True(
            _world.Calls == calls,
            $"expected {calls} git call(s), saw {_world.Calls}:{Environment.NewLine}{DescribeCalls()}");

    /// <summary>Asserts every repo was read as level with origin/main.</summary>
    [Then(@"^every local base is current$")]
    public void ThenEveryLocalBaseIsCurrent()
    {
        Assert.NotEmpty(_world.RepoNames);
        Assert.All(_world.RepoNames, name =>
        {
            var finding = _world.BaseFinding(name);
            Assert.True(
                finding is not null,
                $"repo {name} had no local-base finding. All findings: {_world.DescribeFindings()}");
            Assert.True(
                finding.Status == CheckStatus.Ok,
                $"repo {name} local base should be current, but got {finding.Status}: {finding.Detail}");
        });
    }

    /// <summary>The waterfall invariant: a repo costs one spawn, not one per ref or per attribute.</summary>
    [Then(@"^the read makes one git call per repo$")]
    public void ThenTheReadMakesOneGitCallPerRepo() =>
        Assert.True(
            _world.Calls == _world.RepoNames.Count,
            $"expected one git call per repo ({_world.RepoNames.Count}), saw {_world.Calls}:{Environment.NewLine}{DescribeCalls()}");

    /// <summary>A generous ceiling that still catches a read that degenerates into a chain per repo.</summary>
    [Then(@"^the read finishes within (\d+) seconds$")]
    public void ThenTheReadFinishesWithin(int seconds) =>
        Assert.True(
            _world.Duration < TimeSpan.FromSeconds(seconds),
            $"the read took {_world.Duration.TotalSeconds:F2} s for {_world.RepoNames.Count} repos, over the {seconds} s budget");

    /// <inheritdoc />
    [AfterScenario]
    public void Dispose() => _world.Dispose();

    private string DescribeCalls() =>
        _world.Requests.Count == 0
            ? "(no git calls)"
            : string.Join(Environment.NewLine, _world.Requests.Select(request => "  git " + string.Join(' ', request)));
}
