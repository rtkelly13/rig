namespace Rig.Core;

/// <summary>
/// The exit code every rig command returns (docs/target-architecture.md, "One shape for every feature").
/// </summary>
public enum ExitCode
{
    /// <summary>Observed state matches desired state, or the apply finished.</summary>
    Clean = 0,

    /// <summary>The plan is not empty: observed state differs from desired state.</summary>
    Drift = 1,

    /// <summary>The command could not read, plan or apply.</summary>
    Error = 2,
}

/// <summary>Maps a plan's outcome to an <see cref="ExitCode"/>.</summary>
public static class ExitCodes
{
    /// <summary>Returns <see cref="ExitCode.Drift"/> when there are changes, otherwise <see cref="ExitCode.Clean"/>.</summary>
    /// <param name="changeCount">How many changes the plan holds.</param>
    /// <returns>The exit code for the plan.</returns>
    public static ExitCode ForPlan(int changeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(changeCount);
        return changeCount == 0 ? ExitCode.Clean : ExitCode.Drift;
    }
}
