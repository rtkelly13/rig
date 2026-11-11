namespace Rig.Core.Policy;

/// <summary>Operational risk tier for a command invocation, lowest first; the file's tier names in that order.</summary>
public enum CommandRisk
{
    /// <summary>Read-only or validation: allowed.</summary>
    Safe,

    /// <summary>Changes the local machine: asks first.</summary>
    Local,

    /// <summary>Reaches outside the machine: asks first.</summary>
    External,

    /// <summary>Blocked outright; also every command no rule matches.</summary>
    Denied,
}

/// <summary>Outcome of projecting the policy into one agent's native configuration.</summary>
public enum ProjectionStatus
{
    /// <summary>The native file was written.</summary>
    Applied,

    /// <summary>The native file already held the policy.</summary>
    Unchanged,

    /// <summary>The native file would have been written.</summary>
    DryRun,

    /// <summary>The agent has no native permission file.</summary>
    Unsupported,

    /// <summary>The native file could not be read or written.</summary>
    Failed,
}

/// <summary>The names the policy file and the CLI print for <see cref="CommandRisk"/> and <see cref="ProjectionStatus"/>.</summary>
public static class PolicyNames
{
    /// <summary>The tier's name in the policy file: <c>safe</c>, <c>local</c>, <c>external</c> or <c>denied</c>.</summary>
    public static string Name(this CommandRisk risk) => risk switch
    {
        CommandRisk.Safe => "safe",
        CommandRisk.Local => "local",
        CommandRisk.External => "external",
        _ => "denied",
    };

    /// <summary>The status as printed: <c>applied</c>, <c>unchanged</c>, <c>dry-run</c>, <c>unsupported</c> or <c>failed</c>.</summary>
    public static string Name(this ProjectionStatus status) => status switch
    {
        ProjectionStatus.Applied => "applied",
        ProjectionStatus.Unchanged => "unchanged",
        ProjectionStatus.DryRun => "dry-run",
        ProjectionStatus.Unsupported => "unsupported",
        _ => "failed",
    };

    /// <summary>The tier a policy-file key names, or null when it names none.</summary>
    public static CommandRisk? ParseRisk(string name) => name switch
    {
        "safe" => CommandRisk.Safe,
        "local" => CommandRisk.Local,
        "external" => CommandRisk.External,
        "denied" => CommandRisk.Denied,
        _ => null,
    };
}

/// <summary>A glob pattern and its policy tier.</summary>
/// <param name="Pattern">The <c>fnmatch</c> glob a whole command line is matched against.</param>
/// <param name="Risk">The tier a match puts the command in.</param>
/// <param name="Rationale">Why the rule exists.</param>
/// <param name="ExcludeSystems">Agent surfaces the rule is not projected into, such as <c>claude</c>.</param>
public sealed record CommandRule(string Pattern, CommandRisk Risk, string Rationale, IReadOnlyList<string> ExcludeSystems)
{
    /// <summary>A rule with no rationale that every surface receives.</summary>
    public CommandRule(string pattern, CommandRisk risk)
        : this(pattern, risk, string.Empty, [])
    {
    }
}

/// <summary>A command discovered from a project's checked-in manifest.</summary>
/// <param name="Project">The project folder's name.</param>
/// <param name="Manifest">The <c>package.json</c> or <c>pyproject.toml</c> it came from.</param>
/// <param name="Name">The script's name.</param>
/// <param name="Invocation">How it is run: <c>pnpm name</c> or <c>uv run name</c>.</param>
/// <param name="Risk">The tier its name and source suggest.</param>
/// <param name="Source">The script's command or entry-point target.</param>
public sealed record ProjectCommand(string Project, string Manifest, string Name, string Invocation, CommandRisk Risk, string Source);

/// <summary>Result of one native permission projection.</summary>
/// <param name="System">The agent surface.</param>
/// <param name="Path">Its native permission file, or null when it has none.</param>
/// <param name="Status">What happened.</param>
/// <param name="Detail">A sentence saying so, or the error.</param>
public sealed record ProjectionResult(string System, string? Path, ProjectionStatus Status, string Detail);
