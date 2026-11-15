using Reqnroll;

namespace Rig.Specs.StepDefinitions;

/// <summary>
/// Chooses how a scenario executes. Every step that can touch the world has two executors: the pure domain in memory
/// (the default, no disk and no git) and real git worktrees and files on disk.
/// </summary>
/// <remarks>
/// A scenario tagged <c>@real</c> always runs on disk. <c>RIG_SPEC_MODE=real</c> switches every scenario, which is how
/// a local run can rehearse what CI's regression pass does. Scenarios tagged <c>@regression</c> are skipped outside CI
/// unless <c>RIG_REGRESSION=1</c> is set, so the rapid local pass stays in memory.
/// </remarks>
public static class SpecMode
{
    /// <summary>Run this scenario against real git repositories and files.</summary>
    public const string RealTag = "real";

    /// <summary>Excluded from the rapid local pass; CI runs them.</summary>
    public const string RegressionTag = "regression";

    /// <summary>The mode switch: <c>real</c> selects the on-disk executor.</summary>
    public const string ModeVariable = "RIG_SPEC_MODE";

    /// <summary>Set to <c>1</c> to run the <c>@regression</c> scenarios locally.</summary>
    public const string RegressionVariable = "RIG_REGRESSION";

    /// <summary>True when <paramref name="scenario"/> runs on disk: tagged <c>@real</c> or the mode variable says so.</summary>
    public static bool Real(ScenarioContext scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return IsRealMode() || scenario.ScenarioInfo.Tags.Contains(RealTag, StringComparer.Ordinal);
    }

    /// <summary>Whether the <c>@regression</c> scenarios may run here: CI, an explicit opt-in, or the on-disk pass.</summary>
    public static bool RegressionEnabled => IsRealMode() || IsTruthy(Environment.GetEnvironmentVariable(RegressionVariable))
        || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsRealMode() => string.Equals(Environment.GetEnvironmentVariable(ModeVariable), "real", StringComparison.OrdinalIgnoreCase);

    private static bool IsTruthy(string? value) => value is not null && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
}
