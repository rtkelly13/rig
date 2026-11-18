using System.Text;
using Spectre.Console;

namespace Rig.Cli.Rendering;

/// <summary>
/// A titled table shared by every <c>Rig.Cli</c> command. Its plain <see cref="Render"/> form is the machine-safe
/// text contract existing callers and tests rely on; <see cref="RenderTo"/> renders the same rows through
/// Spectre.Console when a command owns an <see cref="IAnsiConsole"/>.
/// </summary>
/// <param name="title">The line above the table, or null for none.</param>
/// <param name="columns">Each column's heading and whether its cells align right.</param>
internal sealed class TextTable(string? title, IReadOnlyList<(string Heading, bool Right)> columns)
{
    private readonly List<string[]> rows = [];

    /// <summary>Adds a row; it must have a cell per column.</summary>
    /// <param name="cells">One cell per column.</param>
    public TextTable Add(params string[] cells)
    {
        if (cells.Length != columns.Count)
        {
            throw new ArgumentException($"A row needs {columns.Count} cells, not {cells.Length}.", nameof(cells));
        }

        rows.Add(cells);
        return this;
    }

    /// <summary>The title (when there is one), the headings and the rows, each line ending in a newline, trailing spaces trimmed.</summary>
    public string Render()
    {
        string[] headings = [.. columns.Select(c => c.Heading)];
        var widths = columns.Select((_, i) => rows.Select(r => r[i].Length).Prepend(headings[i].Length).Max()).ToArray();
        var text = new StringBuilder();
        if (title is not null)
        {
            text.Append(title).Append('\n');
        }

        foreach (var row in rows.Prepend(headings))
        {
            var cells = row.Select((cell, i) => columns[i].Right ? cell.PadLeft(widths[i]) : cell.PadRight(widths[i]));
            text.Append(string.Join("  ", cells).TrimEnd()).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Renders the table through <paramref name="console"/> with escaped cells and a rounded border.</summary>
    /// <param name="console">The output console.</param>
    public void RenderTo(IAnsiConsole console)
    {
        console.Write(ToTable());
    }

    /// <summary>The same rows as a Spectre table, safe for markup-hostile values such as <c>[P1]</c>.</summary>
    public Table ToTable()
    {
        var table = new Table().RoundedBorder();
        if (title is not null)
        {
            table.Title(TextSanitizer.Markup(title));
        }

        foreach (var (heading, right) in columns)
        {
            var column = new TableColumn(TextSanitizer.Markup(heading));
            if (right)
            {
                column.Alignment = Justify.Right;
            }

            table.AddColumn(column);
        }

        foreach (var row in rows)
        {
            table.AddRow([.. row.Select(cell => (Spectre.Console.Rendering.IRenderable)new Markup(TextSanitizer.Markup(cell)))]);
        }

        return table;
    }
}
