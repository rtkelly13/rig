using System.Globalization;
using System.Text;

namespace Rig.LocReport;

/// <summary>Line counts for one file or a group of files.</summary>
internal readonly record struct Lines(int Code, int Blank, int Doc, int Comment)
{
    public int Total => Code + Blank + Doc + Comment;

    public static Lines operator +(Lines a, Lines b) => new(a.Code + b.Code, a.Blank + b.Blank, a.Doc + b.Doc, a.Comment + b.Comment);
}

/// <summary>Counts lines under <c>src/</c>. Read-only; it never fails on what it finds.</summary>
internal static class Counter
{
    /// <summary>Classifies each line: blank, <c>///</c> doc, <c>//</c> comment, otherwise code.</summary>
    public static Lines Count(string text)
    {
        var lines = new Lines();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            lines += line.Length == 0 ? new Lines(0, 1, 0, 0)
                : line.StartsWith("///", StringComparison.Ordinal) ? new Lines(0, 0, 1, 0)
                : line.StartsWith("//", StringComparison.Ordinal) ? new Lines(0, 0, 0, 1)
                : new Lines(1, 0, 0, 0);
        }

        // A trailing newline yields one phantom empty segment.
        return text.EndsWith('\n') ? lines with { Blank = lines.Blank - 1 } : lines;
    }

    /// <summary>The area of a file under src: <c>Project/Folder</c>, or <c>Project</c> for files directly in the project.</summary>
    public static string Area(string srcRelativePath)
    {
        var parts = srcRelativePath.Replace('\\', '/').Split('/');
        return parts.Length >= 3 ? $"{parts[0]}/{parts[1]}" : parts[0];
    }

    /// <summary>All <c>.cs</c> files under src, skipping build output.</summary>
    public static IEnumerable<string> Files(string src) =>
        Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(src, f).Replace('\\', '/').Split('/').Any(p => p is "bin" or "obj"))
            .Order(StringComparer.Ordinal);

    public static Report Build(string root)
    {
        var src = Path.Join(root, "src");
        var areas = new SortedDictionary<string, Lines>(StringComparer.Ordinal);
        var words = Markers.Words.ToDictionary(w => w.Label, _ => 0, StringComparer.Ordinal);
        var helpers = Markers.Helpers.ToDictionary(h => h, _ => 0, StringComparer.Ordinal);
        if (Directory.Exists(src))
        {
            foreach (var file in Files(src))
            {
                var text = File.ReadAllText(file);
                var counted = Count(text);
                var area = Area(Path.GetRelativePath(src, file));
                areas[area] = areas.GetValueOrDefault(area) + counted;
                foreach (var (label, pattern) in Markers.Words)
                {
                    words[label] += pattern.Count(text);
                }

                var name = Path.GetFileNameWithoutExtension(file);
                if (helpers.ContainsKey(name))
                {
                    helpers[name] += counted.Total;
                }
            }
        }

        return new Report(areas, words, helpers);
    }
}

/// <summary>The measured numbers and how to print them.</summary>
internal sealed record Report(IReadOnlyDictionary<string, Lines> Areas, IReadOnlyDictionary<string, int> Words, IReadOnlyDictionary<string, int> Helpers)
{
    public Lines Total => Areas.Values.Aggregate(new Lines(), (a, b) => a + b);

    public string Render(bool markdown)
    {
        var sb = new StringBuilder();
        var nl = "\n";
        string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        void Table(string title, string[] header, IEnumerable<string[]> rows)
        {
            if (markdown)
            {
                sb.Append("### ").Append(title).Append(nl).Append(nl);
                sb.Append("| ").Append(string.Join(" | ", header)).Append(" |").Append(nl);
                sb.Append('|').Append(string.Concat(header.Select((_, i) => i == 0 ? " --- |" : " ---: |"))).Append(nl);
                foreach (var row in rows)
                {
                    sb.Append("| ").Append(string.Join(" | ", row)).Append(" |").Append(nl);
                }

                sb.Append(nl);
                return;
            }

            sb.Append(title).Append(nl);
            var all = rows.Prepend(header).ToArray();
            var widths = header.Select((_, i) => all.Max(r => r[i].Length)).ToArray();
            foreach (var row in all)
            {
                sb.Append("  ").Append(string.Join("  ", row.Select((c, i) => i == 0 ? c.PadRight(widths[i]) : c.PadLeft(widths[i])))).Append(nl);
            }

            sb.Append(nl);
        }

        var t = Total;
        Table(
            "C# lines per area under src/ (code excludes blank, // and /// lines)",
            ["Area", "Code", "Blank", "Doc (///)", "Comment (//)"],
            Areas.Select(a => new[] { a.Key, N(a.Value.Code), N(a.Value.Blank), N(a.Value.Doc), N(a.Value.Comment) })
                .Append(["Total", N(t.Code), N(t.Blank), N(t.Doc), N(t.Comment)]));
        Table(
            "Python carry-overs in src/**/*.cs",
            ["Marker", "Count"],
            Words.Select(w => new[] { w.Key, N(w.Value) }));
        Table(
            "Helpers kept to match Python's output (file line count)",
            ["Helper", "Lines"],
            Helpers.Select(h => new[] { h.Key, N(h.Value) }));
        return sb.ToString().TrimEnd('\n') + nl;
    }
}
