namespace Rig.Core.Desktop;

/// <summary>
/// A feature toggle naming a feature the plan does not declare, or one both enabled and disabled. A silent no-op would
/// be the worst outcome: the caller asked for a different workspace and would get the current one.
/// </summary>
public sealed class UnknownFeatureException : Exception
{
    /// <summary>An empty error.</summary>
    public UnknownFeatureException()
    {
    }

    /// <summary>An error saying <paramref name="message"/>.</summary>
    public UnknownFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>An error saying <paramref name="message"/>, caused by <paramref name="inner"/>.</summary>
    public UnknownFeatureException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>One rig flag as a plan sees it: its default and any override stored with <c>rig flags enable|disable</c>.</summary>
/// <param name="Name">The flag's name.</param>
/// <param name="Default">Its registered default.</param>
/// <param name="Override">The stored override, if any.</param>
public sealed record PlanFlag(string Name, bool Default, bool? Override = null);

/// <summary>
/// Feature flags and the logon deferral applied to a loaded plan. Ported from <c>desktop_manager.plan</c>
/// (<c>resolve_feature_flags</c>, <c>apply_feature_flags</c>, <c>with_stored_flags</c>, <c>defer_logon_commands</c>).
/// </summary>
public static class PlanFeatures
{
    /// <summary>
    /// Each declared feature's effective state: the plan's <c>features</c> are the defaults, and
    /// <paramref name="enable"/> and <paramref name="disable"/> override them. Naming a feature in both is a conflict,
    /// not a last-one-wins guess.
    /// </summary>
    public static OrderedDictionary<string, bool> Resolve(WorkspacePlan plan, IReadOnlyCollection<string>? enable = null, IReadOnlyCollection<string>? disable = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        enable ??= [];
        disable ??= [];
        var resolved = new OrderedDictionary<string, bool>(plan.Features, StringComparer.Ordinal);
        var unknown = enable.Concat(disable).Where(name => !resolved.ContainsKey(name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            var known = resolved.Count > 0 ? string.Join(", ", resolved.Keys.Order(StringComparer.Ordinal)) : "<none declared>";
            throw new UnknownFeatureException($"Unknown feature(s): {string.Join(", ", unknown)}. Declared features: {known}");
        }

        var conflicting = enable.Intersect(disable, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (conflicting.Count > 0)
        {
            throw new UnknownFeatureException($"Feature(s) both enabled and disabled: {string.Join(", ", conflicting)}");
        }

        foreach (var name in enable)
        {
            resolved[name] = true;
        }

        foreach (var name in disable)
        {
            resolved[name] = false;
        }

        return resolved;
    }

    /// <summary>
    /// <paramref name="plan"/> with entries behind switched-off features disabled. Feature state collapses onto each
    /// entry's <c>disabled</c>, which every consumer already honours; an entry disabled in the file stays disabled.
    /// A switched-off desktop is dropped rather than emptied, because the desktop count is the length of the list, and
    /// its name goes to <see cref="WorkspacePlan.RetiredDesktopNames"/> so restore can remove a leftover virtual desktop.
    /// </summary>
    public static WorkspacePlan Apply(WorkspacePlan plan, IReadOnlyCollection<string>? enable = null, IReadOnlyCollection<string>? disable = null)
    {
        var resolved = Resolve(plan, enable, disable);
        if (resolved.Count == 0)
        {
            return plan;
        }

        bool Off(string? feature) => feature is not null && !resolved.GetValueOrDefault(feature);
        bool SwitchedOff(DesktopPlan desktop) => desktop.Disabled || Off(desktop.Feature);

        return plan with
        {
            Desktops =
            [
                .. plan.Desktops.Where(desktop => !SwitchedOff(desktop)).Select(desktop => desktop with
                {
                    Launch = [.. desktop.Launch.Select(entry => Off(entry.Feature) && !entry.Disabled ? entry with { Disabled = true } : entry)],
                }),
            ],
            Features = resolved,
            RetiredDesktopNames = [.. plan.Desktops.Where(SwitchedOff).Select(desktop => desktop.Name)],
        };
    }

    /// <summary>
    /// <paramref name="plan"/> with every rig flag declared, layered default &lt; the plan's <c>features</c> &lt; stored
    /// override. The flags registry holds each default, so the machine plan needs no <c>features</c> block; a plan that
    /// sets a flag pins it for that plan, and an override beats both.
    /// </summary>
    public static WorkspacePlan WithFlags(WorkspacePlan plan, IEnumerable<PlanFlag> flags)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(flags);
        var states = flags.ToList();
        var features = new OrderedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var flag in states)
        {
            features[flag.Name] = flag.Default;
        }

        foreach (var (name, on) in plan.Features)
        {
            features[name] = on;
        }

        foreach (var flag in states)
        {
            if (flag.Override is { } on)
            {
                features[flag.Name] = on;
            }
        }

        return plan with { Features = features };
    }

    /// <summary>Enabled terminal entries whose command waits for <c>claude-sessions startup</c> at logon, in plan order.</summary>
    public static IReadOnlyList<LaunchEntry> DeferredLogonTerminals(WorkspacePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. plan.Desktops.Where(desktop => !desktop.Disabled).SelectMany(desktop => desktop.Launch).Where(entry => !entry.Disabled && entry.DefersCommandAtLogon)];
    }

    /// <summary>
    /// The plan a logon restore runs: entries marked <c>command_at_logon: "deferred"</c> open a bare shell. Only
    /// <c>workspace restore --defer-logon-commands</c> applies this, so a terminal is never left as a bare shell by a
    /// restore nothing follows up on.
    /// </summary>
    public static WorkspacePlan DeferLogonCommands(WorkspacePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan with
        {
            Desktops = [.. plan.Desktops.Select(desktop => desktop with { Launch = [.. desktop.Launch.Select(entry => entry.DefersCommandAtLogon ? entry with { Command = null } : entry)] })],
        };
    }
}
