using Rig.Cli.Rendering;
using Rig.Core.Reconciliation;
using Spectre.Console;

namespace Rig.Cli.Tests.Rendering;

/// <summary>
/// The shared output seam: a machine-safe console, escaped dynamic text, and the plan/verdict/property vocabulary
/// every command renders through.
/// </summary>
public sealed class SpectreOutputExtensionsTests
{
    [Fact]
    public void A_StringWriter_is_machine_safe_so_captured_output_has_no_ansi()
    {
        using var writer = new StringWriter();
        var console = RigConsole.Create(writer);

        console.MarkupLine("[red bold]failure[/]");

        Assert.False(RigConsole.IsInteractive(writer, _ => null));
        Assert.DoesNotContain('\u001b', writer.ToString());
        Assert.Contains("failure", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicitly_interactive_console_keeps_ansi()
    {
        using var writer = new StringWriter();

        RigConsole.Create(writer, interactive: true).MarkupLine("[red bold]failure[/]");

        Assert.Contains('\u001b', writer.ToString());
    }

    [Fact]
    public void Headless_and_no_color_switches_force_machine_output_in_a_terminal()
    {
        Assert.False(RigConsole.IsInteractive(Console.Out, name => name == RigConsole.HeadlessVariable ? "1" : null));
        Assert.False(RigConsole.IsInteractive(Console.Out, name => name == RigConsole.NoColorVariable ? "1" : null));
        Assert.False(RigConsole.IsInteractive(Console.Out, name => name == RigConsole.CiVariable ? "true" : null));
        Assert.False(RigConsole.IsInteractive(Console.Out, name => name == "TERM" ? "dumb" : null));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("[P1] critical", "[[P1]] critical")]
    [InlineData("[core.hooksPath]", "[[core.hooksPath]]")]
    public void Markup_escapes_brackets_so_labels_print_as_written(string value, string expected)
    {
        Assert.Equal(expected, TextSanitizer.Markup(value));
    }

    [Fact]
    public void Ansi_sequences_are_stripped_before_text_reaches_markup()
    {
        Assert.Equal(string.Empty, TextSanitizer.StripAnsi(null));
        Assert.Equal("red", TextSanitizer.StripAnsi("\u001b[31mred\u001b[0m"));
        Assert.Equal("[P1]", TextSanitizer.StripAnsi("\u001b[1m[P1]\u001b[0m"));
        Assert.Equal("link", TextSanitizer.StripAnsi("\u001b]8;;https://example.test\u0007link\u001b]8;;\u0007"));
    }

    [Fact]
    public void Plan_renders_change_badges_subjects_and_details_without_markup_mangling()
    {
        using var writer = new StringWriter();
        var console = RigConsole.Create(writer);
        var plan = new FakePlan(
        [
            new FakeChange("[core.hooksPath]", "Create the missing hook path", ChangeKind.WouldChange),
            new FakeChange("[P1] review thread", "Delete the stale thread", ChangeKind.WouldChange),
            new FakeChange("protected/branch", "Refused by a guard", ChangeKind.Refused),
            new FakeChange("src/a.cs", "Already aligned", ChangeKind.Aligned),
        ]);

        console.RenderPlan(plan);

        var text = writer.ToString();
        Assert.Contains("would-change", text, StringComparison.Ordinal);
        Assert.Contains("refused", text, StringComparison.Ordinal);
        Assert.Contains("aligned", text, StringComparison.Ordinal);
        Assert.Contains("[core.hooksPath]", text, StringComparison.Ordinal);
        Assert.Contains("[P1] review thread", text, StringComparison.Ordinal);
        Assert.Contains("2 of 4 change(s) to apply", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public void A_clean_plan_says_so()
    {
        using var writer = new StringWriter();
        var console = RigConsole.Create(writer);

        console.RenderPlan(new FakePlan([new FakeChange("src/a.cs", "Already aligned", ChangeKind.Aligned)]));

        Assert.Contains("no drift", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("Clean.", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_with_errors_renders_error_verdict()
    {
        using var writer = new StringWriter();
        var console = RigConsole.Create(writer);

        console.RenderPlan(new FakePlan([new FakeChange("src/a.cs", "Failed", ChangeKind.Error)]));

        Assert.Contains("Errors in plan.", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Clean.", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_renders_pass_and_fail_panels_with_escaped_detail()
    {
        using var passWriter = new StringWriter();
        using var failWriter = new StringWriter();

        RigConsole.Create(passWriter).RenderVerdict("Healthy", ok: true, "11 checks passed");
        RigConsole.Create(failWriter).RenderVerdict("[P1] Issues Found", ok: false, "[core.hooksPath] is unset");

        Assert.Contains("PASS  Healthy", passWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains("11 checks passed", passWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains("FAIL  [P1] Issues Found", failWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains("[core.hooksPath] is unset", failWriter.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', failWriter.ToString());
    }

    [Fact]
    public void Properties_render_in_ordinal_key_order_with_escaped_names_and_values()
    {
        using var writer = new StringWriter();
        var console = RigConsole.Create(writer);

        console.RenderProperties(
            "[P1] Metadata",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["z"] = "[core.hooksPath]",
                ["a"] = "first",
            });

        var text = writer.ToString();
        Assert.Contains("[P1] Metadata", text, StringComparison.Ordinal);
        Assert.Contains("[core.hooksPath]", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("│ a", StringComparison.Ordinal) < text.IndexOf("│ z", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public void A_table_keeps_its_plain_machine_form_and_escapes_when_rendered_through_spectre()
    {
        var table = new TextTable("Findings", [("path", false), ("count", true)])
            .Add("[core.hooksPath]", "2")
            .Add("src/a.cs", "1");

        Assert.Contains("Findings\npath", table.Render(), StringComparison.Ordinal);
        Assert.Contains("[core.hooksPath]", table.Render(), StringComparison.Ordinal);

        using var writer = new StringWriter();
        table.RenderTo(RigConsole.Create(writer));
        Assert.Contains("[core.hooksPath]", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', writer.ToString());
    }

    [Fact]
    public void A_table_row_must_match_the_column_count()
    {
        var table = new TextTable("t", [("a", false)]);
        Assert.Throws<ArgumentException>(() => table.Add("1", "2"));
    }

    [Fact]
    public void Badge_maps_all_change_kinds_and_fallback()
    {
        Assert.Equal("[grey]aligned[/]", SpectreOutputExtensions.Badge(ChangeKind.Aligned));
        Assert.Equal("[yellow]would-change[/]", SpectreOutputExtensions.Badge(ChangeKind.WouldChange));
        Assert.Equal("[green]changed[/]", SpectreOutputExtensions.Badge(ChangeKind.Changed));
        Assert.Equal("[darkorange]refused[/]", SpectreOutputExtensions.Badge(ChangeKind.Refused));
        Assert.Equal("[red]error[/]", SpectreOutputExtensions.Badge(ChangeKind.Error));
        Assert.Equal("[grey]99[/]", SpectreOutputExtensions.Badge((ChangeKind)99));
    }

    [Fact]
    public void RenderPlan_throws_on_null_arguments()
    {
        using var writer = new StringWriter();
        var console = RigConsole.Create(writer);
        var plan = new FakePlan([]);

        Assert.Throws<ArgumentNullException>(() => SpectreOutputExtensions.RenderPlan<FakeChange>(null!, plan));
        Assert.Throws<ArgumentNullException>(() => SpectreOutputExtensions.RenderPlan<FakeChange>(console, null!));
    }

    private sealed record FakeChange(string Subject, string Detail, ChangeKind Kind) : IChange;

    private sealed record FakePlan(IReadOnlyList<FakeChange> Changes) : IPlan<FakeChange>
    {
        public IReadOnlyList<string> Errors => [.. Changes.Where(c => c.Kind == ChangeKind.Error).Select(c => $"{c.Subject}: {c.Detail}")];
    }
}
