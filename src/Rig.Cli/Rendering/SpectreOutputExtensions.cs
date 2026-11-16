using Spectre.Console;

namespace Rig.Cli.Rendering;

/// <summary>
/// The shared rendering vocabulary for <c>Rig.Cli</c>: the verdict panel and the escaped cells every table uses.
/// Dynamic text is always sanitised, so brackets in a path or a review-thread label never become markup.
/// </summary>
internal static class SpectreOutputExtensions
{
    /// <summary>Renders a pass/fail verdict panel with an optional detail line.</summary>
    /// <param name="console">The output console.</param>
    /// <param name="title">The verdict, such as <c>Clean</c> or <c>Issues Found</c>.</param>
    /// <param name="ok">Whether the verdict passed.</param>
    /// <param name="detail">The reason, a count, or the command that resolves it.</param>
    public static void RenderVerdict(this IAnsiConsole console, string title, bool ok, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(console);

        var color = ok ? "green" : "red";
        var badge = ok ? "PASS" : "FAIL";
        var body = $"[{color} bold]{badge}[/]  {TextSanitizer.Markup(title)}";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            body += $"\n[grey]{TextSanitizer.Markup(detail)}[/]";
        }

        var panel = new Panel(new Markup(body))
            .RoundedBorder()
            .BorderStyle(new Style(ok ? Color.Green : Color.Red))
            .Padding(1, 0, 1, 0);
        console.Write(panel);
        console.WriteLine();
    }
}
