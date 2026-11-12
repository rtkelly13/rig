using System.Text;

namespace Rig.Cli.Vault;

/// <summary>A titled plain-text table: columns padded to their widest cell, numbers right-aligned.</summary>
/// <param name="title">The line above the table, or null for none.</param>
/// <param name="columns">Each column's heading and whether its cells align right.</param>
internal sealed class TextTable(string? title, IReadOnlyList<(string Heading, bool Right)> columns)
{
    private readonly List<string[]> rows = [];

    /// <summary>Adds a row; it must have a cell per column.</summary>
    public TextTable Add(params string[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
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
}
