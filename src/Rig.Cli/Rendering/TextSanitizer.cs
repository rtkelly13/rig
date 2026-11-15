using System.Text.RegularExpressions;

namespace Rig.Cli.Rendering;

/// <summary>Cleans dynamic text before it reaches Spectre's markup parser or a machine-readable stream.</summary>
internal static partial class TextSanitizer
{
    /// <summary>Removes ANSI escape sequences (CSI and the two-byte ESC forms) from <paramref name="text"/>.</summary>
    /// <param name="text">A value from git, a process, a path, or any other source rig does not control.</param>
    public static string StripAnsi(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : AnsiEscape().Replace(text, string.Empty);

    /// <summary>
    /// Makes <paramref name="text"/> safe to embed in a Spectre markup string: ANSI is stripped and every bracket
    /// (and other markup character) is escaped, so <c>[P1]</c> or <c>[core.hooksPath]</c> prints as written.
    /// </summary>
    /// <param name="text">The dynamic value.</param>
    public static string Markup(string? text) => Spectre.Console.Markup.Escape(StripAnsi(text));

    // CSI: ESC [ parameters intermediates final byte. OSC covers terminal hyperlinks; the two-byte forms cover the rest.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-_])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscape();
}
