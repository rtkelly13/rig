using Reqnroll;
using Reqnroll.UnitTestProvider;

namespace Rig.Specs.StepDefinitions;

/// <summary>Applies the mode and tag rules before a scenario's first step runs.</summary>
[Binding]
public sealed class SpecHooks(IUnitTestRuntimeProvider runtime, ScenarioContext scenario)
{
    /// <summary>
    /// Skips a <c>@regression</c> scenario when only the rapid local pass is running. <c>TestIgnore</c> becomes xUnit
    /// v3's dynamic skip, so the run passes with a counted skip rather than reporting a failure.
    /// </summary>
    [BeforeScenario(Order = 0)]
    public void SkipRegressionOutsideCi()
    {
        if (scenario.ScenarioInfo.Tags.Contains(SpecMode.RegressionTag, StringComparer.Ordinal) && !SpecMode.RegressionEnabled)
        {
            runtime.TestIgnore($"@regression runs in CI; set {SpecMode.RegressionVariable}=1 or {SpecMode.ModeVariable}=real to run it locally");
        }
    }
}
