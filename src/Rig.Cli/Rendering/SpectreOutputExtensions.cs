using Rig.Core.Reconciliation;
using Spectre.Console;

namespace Rig.Cli.Rendering;

/// <summary>
/// The shared rendering vocabulary for <c>Rig.Cli</c>: plans, verdicts, properties, and the escaped cells every
/// table uses. Dynamic text is always sanitised, so brackets in a path or a review-thread label never become markup.
/// </summary>
internal static class SpectreOutputExtensions
{
    /// <summary>Renders a reconciliation plan as a rounded table of change badges, targets and descriptions.</summary>
    /// <typeparam name="TChange">The plan's change type.</typeparam>
    /// <param name="console">The output console.</param>
    /// <param name="plan">The typed plan to render.</param>
    public static void RenderPlan<TChange>(this IAnsiConsole console, IPlan<TChange> plan)
        where TChange : IChange
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(plan);

        var summary = plan.IsClean
            ? "no drift — nothing to apply"
            : $"{Count(plan.DriftCount)} of {Count(plan.Changes.Count)} change(s) to apply";
        var table = new Table()
            .RoundedBorder()
            .Title(TextSanitizer.Markup($"Plan — {summary}"));
        table.AddColumn(TextSanitizer.Markup("Change"));
        table.AddColumn(TextSanitizer.Markup("Target"));
        table.AddColumn(TextSanitizer.Markup("Description"));

        foreach (var change in plan.Changes)
        {
            table.AddRow(
                new Markup(Badge(change.Kind)),
                new Markup(TextSanitizer.Markup(change.Target)),
                new Markup(TextSanitizer.Markup(change.Description)));
        }

        console.Write(table);
        console.MarkupLine(plan.IsClean ? "[green]Clean.[/]" : "[yellow]Review before applying.[/]");
    }

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

    /// <summary>Renders key/value metadata as a rounded two-column inspection table.</summary>
    /// <param name="console">The output console.</param>
    /// <param name="title">The table title.</param>
    /// <param name="properties">The property names and values; rendered in ordinal key order.</param>
    public static void RenderProperties(this IAnsiConsole console, string title, IReadOnlyDictionary<string, string> properties)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(properties);

        var table = new Table()
            .RoundedBorder()
            .Title(TextSanitizer.Markup(title));
        table.AddColumn(TextSanitizer.Markup("Property"));
        table.AddColumn(TextSanitizer.Markup("Value"));

        foreach (var pair in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            table.AddRow(new Markup(TextSanitizer.Markup(pair.Key)), new Markup(TextSanitizer.Markup(pair.Value)));
        }

        console.Write(table);
    }

    /// <summary>The semantic colour badge for a change kind.</summary>
    /// <param name="kind">The planned mutation.</param>
    public static string Badge(ChangeKind kind) => kind switch
    {
        ChangeKind.NoOp => "[grey]no-op[/]",
        ChangeKind.Create => "[green]create[/]",
        ChangeKind.Update => "[yellow]update[/]",
        ChangeKind.Delete => "[red]delete[/]",
        ChangeKind.Refusal => "[darkorange]refuse[/]",
        _ => $"[grey]{TextSanitizer.Markup(kind.ToString())}[/]",
    };

    private static string Count(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
