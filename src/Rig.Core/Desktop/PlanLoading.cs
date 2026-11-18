namespace Rig.Core.Desktop;

/// <summary>
/// Reading a plan the way the desktop commands do: the per-user default path, the logs folder beside it, and the
/// checks <c>load_plan</c> runs before anything uses the result.
/// </summary>
public static class PlanLoading
{
    /// <summary>The environment variable that moves a run's logs away from the plan's folder.</summary>
    public const string LogDirectoryVariable = "DESKTOP_MANAGER_LOG_DIR";

    /// <summary>The per-user plan: under <c>APPDATA</c> on Windows when it is set, else <c>~/.config/workspace-manager</c>.</summary>
    public static string DefaultPath(Func<string, string?> getVariable, string home, bool windows)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        ArgumentNullException.ThrowIfNull(home);
        if (windows && getVariable("APPDATA") is { Length: > 0 } appData)
        {
            return Path.Join(appData, "workspace-manager", "windows-workspace.json");
        }

        return Path.Join(home, ".config", "workspace-manager", "windows-workspace.json");
    }

    /// <summary><c>logs</c> beside the plan, unless <see cref="LogDirectoryVariable"/> names another folder.</summary>
    public static string LogsDirectory(string configPath, Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(configPath);
        ArgumentNullException.ThrowIfNull(getVariable);
        return getVariable(LogDirectoryVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Join(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "logs");
    }

    /// <summary>
    /// The plan as <c>load_plan</c> returns it: the file's own features, every rig flag layered underneath and
    /// <paramref name="enable"/>/<paramref name="disable"/> on top, columns fitted to <paramref name="screenWidths"/>,
    /// then verified. The result is what every desktop command runs against.
    /// </summary>
    /// <exception cref="InvalidPlanException">The plan's entries or placements are invalid.</exception>
    /// <exception cref="UnknownFeatureException">A feature flag names a feature the plan does not declare.</exception>
    public static WorkspacePlan Load(
        string path,
        Func<IReadOnlyList<PlanFlag>> flags,
        IReadOnlyList<string>? enable = null,
        IReadOnlyList<string>? disable = null,
        IReadOnlyDictionary<int, int>? screenWidths = null,
        IPathProbe? paths = null,
        int monitorCount = 0,
        bool verify = true,
        Func<string, string?>? getVariable = null,
        IReadOnlyDictionary<string, string>? machineAliases = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(flags);
        var plan = PlanFiles.Load(path);
        plan = PlanFeatures.WithFlags(plan, flags());
        plan = PlanFeatures.Apply(plan, enable, disable);
        plan = Placement.FitColumns(plan, screenWidths ?? new Dictionary<int, int>());
        if (!verify)
        {
            return plan;
        }

        var substitutions = plan.Substitutions(
            Placeholders.EnvironmentAliases(getVariable ?? (_ => null)),
            machineAliases ?? new Dictionary<string, string>());
        return Verify(plan, path, substitutions, paths, monitorCount: monitorCount);
    }

    /// <summary>
    /// <paramref name="plan"/> after <see cref="Placement.ValidatePlan"/> and <see cref="Placement.ValidatePlanStructure"/>
    /// pass. A plan with entries is "Invalid workspace plan" and one whose layout is wrong is
    /// "Structurally invalid workspace plan".
    /// </summary>
    /// <exception cref="InvalidPlanException">The plan is invalid.</exception>
    public static WorkspacePlan Verify(
        WorkspacePlan plan,
        string source,
        IReadOnlyDictionary<string, string> substitutions,
        IPathProbe? paths = null,
        int? expectedScreenCount = null,
        int monitorCount = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(substitutions);
        var errors = Placement.ValidatePlan(plan, substitutions, monitorCount, paths);
        if (errors.Count > 0)
        {
            throw new InvalidPlanException($"Invalid workspace plan {source}: {string.Join("; ", errors)}");
        }

        var issues = Placement.ValidatePlanStructure(plan, expectedScreenCount);
        if (issues.Count > 0)
        {
            throw new InvalidPlanException($"Structurally invalid workspace plan {source}: {string.Join("; ", issues)}");
        }

        return plan;
    }

    /// <summary>
    /// The machine plan from the repository's bundle template, with <paramref name="workspace"/> and
    /// <paramref name="personalWorkspace"/> filled in. It is verified as <see cref="Load"/> would run it, with
    /// <paramref name="flags"/> applied, so entries behind a switched-off flag don't count; the plan returned keeps
    /// its feature tags, so a later <c>rig flags enable|disable</c> still takes effect. Every entry, switched off or
    /// not, must still pass <see cref="Placement.ValidatePlan"/>, so turning a flag on later cannot reveal a bad entry.
    /// </summary>
    /// <exception cref="InvalidPlanException">The template is not a valid plan.</exception>
    public static WorkspacePlan BuildMachinePlan(string templatePath, string workspace, IEnumerable<PlanFlag> flags, string? personalWorkspace = null)
    {
        ArgumentNullException.ThrowIfNull(templatePath);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(flags);
        var plan = PlanFiles.Load(templatePath) with { Workspace = workspace, PersonalWorkspace = personalWorkspace };
        var errors = Placement.ValidatePlan(plan, plan.Substitutions(new Dictionary<string, string>(), new Dictionary<string, string>()));
        if (errors.Count > 0)
        {
            throw new InvalidPlanException($"Invalid workspace plan {templatePath}: {string.Join("; ", errors)}");
        }

        var effective = Placement.FitColumns(PlanFeatures.Apply(PlanFeatures.WithFlags(plan, flags)), new Dictionary<int, int>());
        _ = Verify(effective, templatePath, effective.Substitutions(new Dictionary<string, string>(), new Dictionary<string, string>()));
        return plan;
    }
}
