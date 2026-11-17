using Rig.Core.Tests;

namespace Rig.LocReport.Tests;

public sealed class CounterTests
{
    [Fact]
    public void Blank_doc_and_comment_lines_are_not_code()
    {
        var lines = Counter.Count("/// <summary>x</summary>\n// note\n\n  \nvar a = 1; // trailing\n    var b = 2;\n");

        Assert.Equal(new Lines(Code: 2, Blank: 2, Doc: 1, Comment: 1), lines);
        Assert.Equal(6, lines.Total);
    }

    [Theory]
    [InlineData("Rig.Core/Jobs/Run.cs", "Rig.Core/Jobs")]
    [InlineData("Rig.Core/Jobs/Deep/Run.cs", "Rig.Core/Jobs")]
    [InlineData("Rig.Cli/Program.cs", "Rig.Cli")]
    public void Files_group_under_project_and_top_level_folder(string path, string area)
    {
        Assert.Equal(area, Counter.Area(path));
    }

    [Fact]
    public void The_report_groups_by_area_skips_build_output_and_counts_markers_and_helpers()
    {
        using var temp = new TempDir();
        Write(temp, "src/Rig.Core/Vault/VaultYaml.cs", "// Python parity\nclass A {}\n\nvar s = SnakeCase(x);\n");
        Write(temp, "src/Rig.Core/Vault/Other.cs", "class B {}\n");
        Write(temp, "src/Rig.Core/Jobs/J.cs", "class C {}\n// Pythonic is not the word\n/// Python and Python\n");
        Write(temp, "src/Rig.Core/obj/Gen.cs", "class Generated {} // Python\n");
        Write(temp, "src/Rig.Cli/Program.cs", "class P {}\n");

        var report = Counter.Build(temp.Path);

        Assert.Equal(["Rig.Cli", "Rig.Core/Jobs", "Rig.Core/Vault"], report.Areas.Keys.ToArray());
        Assert.Equal(new Lines(3, 1, 0, 1), report.Areas["Rig.Core/Vault"]);
        Assert.Equal(new Lines(1, 0, 1, 1), report.Areas["Rig.Core/Jobs"]);
        Assert.Equal(5, report.Total.Code);
        Assert.Equal(3, report.Words["the word Python"]);
        Assert.Equal(1, report.Words["SnakeCase uses"]);
        Assert.Equal(4, report.Helpers["VaultYaml"]);
        Assert.Equal(0, report.Helpers["VaultUrl"]);
    }

    [Fact]
    public void Markdown_output_is_a_table_and_a_missing_src_is_an_empty_report()
    {
        using var temp = new TempDir();
        Write(temp, "src/P/F/a.cs", "class A {}\n");

        var markdown = Counter.Build(temp.Path).Render(markdown: true);
        Assert.Contains("| Area | Code | Blank | Doc (///) | Comment (//) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| P/F | 1 | 0 | 0 | 0 |", markdown, StringComparison.Ordinal);

        using var empty = new TempDir();
        Assert.Equal(0, Counter.Build(empty.Path).Total.Total);
    }

    private static void Write(TempDir temp, string relative, string text) => File.WriteAllText(temp.File(relative), text);
}
