using Spectre.Console;

namespace Rig.Cli.Rendering;

/// <summary>
/// Builds the <see cref="IAnsiConsole"/> a rig command writes through, and decides whether it may use colour.
/// </summary>
/// <remarks>
/// The console is the single rendering seam for <c>Rig.Cli</c>. A real terminal gets ANSI colour; a redirected
/// stream, a test <see cref="StringWriter"/>, or a machine/agent run gets the same layout with ANSI disabled.
/// Set <see cref="HeadlessVariable"/> (or <c>NO_COLOR</c>, <c>CI</c>) to force the plain form even in a terminal.
/// </remarks>
internal static class RigConsole
{
    /// <summary>Set to a truthy value to force machine-safe output (no ANSI, no interaction).</summary>
    public const string HeadlessVariable = "RIG_HEADLESS";

    /// <summary>The conventional no-colour switch, honoured along with <see cref="HeadlessVariable"/>.</summary>
    public const string NoColorVariable = "NO_COLOR";

    /// <summary>The CI switch, whose output is read as a log rather than a terminal.</summary>
    public const string CiVariable = "CI";

    /// <summary>Builds a console over <paramref name="writer"/>.</summary>
    /// <param name="writer">Where the rendered output goes.</param>
    /// <param name="interactive">
    /// Whether the writer is a terminal. When omitted it is detected: only the real standard streams count, and only
    /// when they are not redirected.
    /// </param>
    public static IAnsiConsole Create(TextWriter writer, bool? interactive = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var live = interactive ?? IsInteractive(writer, Environment.GetEnvironmentVariable);
        return AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = live ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = live ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
            // Nothing may silently rewrite the output after a command has decided what it means.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
    }

    /// <summary>Whether output to <paramref name="writer"/> may carry ANSI styling.</summary>
    /// <param name="writer">The output stream.</param>
    /// <param name="environment">Reads an environment variable; <see cref="Environment.GetEnvironmentVariable(string)"/> normally.</param>
    public static bool IsInteractive(TextWriter writer, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(environment);

        if (IsSet(environment(RigConsole.HeadlessVariable))
            || environment(NoColorVariable) is not null
            || IsSet(environment(CiVariable))
            || string.Equals(environment("TERM"), "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Only a real standard stream can be a terminal. A StringWriter in a test, or any writer a caller supplied,
        // is treated as a machine sink so captured output stays free of escape sequences.
        return (ReferenceEquals(writer, Console.Out) && !Console.IsOutputRedirected)
            || (ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected);
    }

    private static bool IsSet(string? value) =>
        value is { Length: > 0 }
        && !string.Equals(value, "0", StringComparison.Ordinal)
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
