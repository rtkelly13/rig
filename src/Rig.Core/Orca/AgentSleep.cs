namespace Rig.Core.Orca;

/// <summary>Agent sleep as Orca has it and as the policy wants it, both written <c>off</c> or <c>on, 30m idle</c>.</summary>
/// <param name="Have">What Orca has.</param>
/// <param name="Want">What the policy wants.</param>
/// <param name="Fix">How to make them agree.</param>
public sealed record AgentSleepCheck(string Have, string Want, string Fix)
{
    /// <summary>Whether Orca has what the policy wants.</summary>
    public bool Ok => Have == Want;

    /// <summary>
    /// Compares Orca's <c>experimentalAgentHibernation</c> settings with <paramref name="want"/>. The runtime rejects these
    /// keys and the database must not be edited while Orca runs, so rig only checks it and names the change to make.
    /// </summary>
    public static AgentSleepCheck Of(AgentSleepPolicy want, bool? enabled, long? idleMs)
    {
        ArgumentNullException.ThrowIfNull(want);

        // Orca reads a missing or out-of-range window as its 30-minute default.
        var minutes = idleMs is >= 60_000 and <= 86_400_000 ? idleMs.Value / 60_000 : AgentSleepPolicy.DefaultMinutes;
        var have = enabled == true ? $"on, {minutes}m idle" : "off";
        return want.Enabled
            ? new(have, $"on, {want.IdleMinutes}m idle", $"Orca Settings → Experimental: turn Agent sleep on with a {want.IdleMinutes} minute idle window")
            : new(have, "off", "Orca Settings → Experimental: turn Agent sleep off");
    }
}

/// <summary>Orca's keep-awake setting as Orca has it and as the policy wants it, written <c>on</c>, <c>off</c> or <c>unset</c>.</summary>
/// <param name="Have">What Orca has.</param>
/// <param name="Want">What the policy wants.</param>
/// <param name="Fix">How to make them agree.</param>
public sealed record KeepAwakeCheck(string Have, string Want, string Fix)
{
    /// <summary>Whether Orca has what the policy wants.</summary>
    public bool Ok => Have == Want;

    /// <summary>
    /// Compares Orca's <c>keepComputerAwakeWhileAgentsRun</c> with <paramref name="want"/>. Like Agent sleep it is only
    /// checked: Orca's database must not be edited while it runs, so rig names the change to make in Orca's Settings.
    /// </summary>
    public static KeepAwakeCheck Of(bool want, bool? have)
    {
        var text = have switch { true => "on", false => "off", null => "unset" };
        var wanted = want ? "on" : "off";
        return new(text, wanted, $"Orca Settings: turn {wanted} Keep computer awake while agents run");
    }
}
