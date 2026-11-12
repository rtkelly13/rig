namespace Rig.Core.Flags;

/// <summary>A flag name that <see cref="FeatureFlags.All"/> does not declare.</summary>
public sealed class UnknownFlagException : Exception
{
    /// <summary>An empty error.</summary>
    public UnknownFlagException()
    {
    }

    /// <summary>An error saying <paramref name="message"/>.</summary>
    public UnknownFlagException(string message)
        : base(message)
    {
    }

    /// <summary>An error saying <paramref name="message"/>, caused by <paramref name="inner"/>.</summary>
    public UnknownFlagException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>One declared feature flag.</summary>
/// <param name="Name">The flag's name, as <c>rig flags enable|disable|reset</c> takes it.</param>
/// <param name="Default">Its state when no override is stored.</param>
/// <param name="Description">What it switches.</param>
public sealed record Flag(string Name, bool Default, string Description);

/// <summary>A declared flag with the override stored for it, if any.</summary>
/// <param name="Flag">The flag.</param>
/// <param name="Override">The stored override, or null when the default applies.</param>
public sealed record FlagState(Flag Flag, bool? Override)
{
    /// <summary>The stored override, else the default.</summary>
    public bool Enabled => Override ?? Flag.Default;
}

/// <summary>
/// The feature flags shared across the rig stack, each declared once with its default and what it switches. Overrides
/// live in <c>rig.db</c> (<see cref="FlagStore"/>).
/// </summary>
/// <remarks>
/// <c>rig.flags.registry</c> keeps a Python copy while <c>desktop_manager</c> still reads flags in Python; the flags
/// oracle test holds the two to one list, and the Python copy goes when its last caller is ported (#85 s15/s28).
/// </remarks>
public static class FeatureFlags
{
    /// <summary>Every declared flag, in the order <c>rig flags list</c> shows them.</summary>
    public static IReadOnlyList<Flag> All { get; } =
    [
        new("orca-sessions", true, "Work desktop: the Work and Personal Orca windows."),
        new("wt-terminals", false, "Work desktop: Windows Terminal sessions each running `claude`."),
        new("warp-terminals", false, "Work desktop: the earlier Warp terminal sessions."),
        new("work-chrome", true, "Work desktop screen 2: the work-profile Chrome window."),
        new("work-fork", false, "Work desktop screen 2: the Fork window, now on the Review desktop."),
        new("personal-desktop", false, "The Personal virtual desktop."),
        new("experimental-desktop", true, "The Lab virtual desktop."),
        new("claude-session-restore", false, "At sign-in, resume cut-off Claude sessions and start `claude` in empty terminals."),
    ];

    /// <summary>The flag called <paramref name="name"/>.</summary>
    /// <exception cref="UnknownFlagException">No flag has that name; the message lists the declared ones.</exception>
    public static Flag Named(string name) =>
        All.FirstOrDefault(flag => flag.Name == name)
        ?? throw new UnknownFlagException(
            $"Unknown flag: {name}. Declared flags: {string.Join(", ", All.Select(flag => flag.Name).Order(StringComparer.Ordinal))}");
}
