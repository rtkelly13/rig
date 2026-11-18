using Rig.Image.Simplifier;

namespace Rig.Image.Tests;

public sealed class SvgSimplifierTests
{
    private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    [Fact]
    public void A_document_with_a_style_sheet_keeps_its_unreferenced_definitions()
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 24 24\"><style>.a{{fill:url(#g)}}</style><defs><linearGradient id=\"g\"/></defs><rect class=\"a\" width=\"4\" height=\"4\"/></svg>";

        var result = SvgSimplifier.Simplify(svg, useGeometric: false);

        Assert.Contains("linearGradient", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<svg><unclosed></svg>")]
    [InlineData("plain text")]
    public void Text_that_is_not_xml_comes_back_unchanged(string text)
    {
        Assert.Equal(text, SvgSimplifier.Simplify(text));
        Assert.Equal(text, SvgSimplifier.Quantize(text));
        Assert.Equal(0, SvgSimplifier.CountPathNodes(text));
    }

    [Fact]
    public void A_length_with_a_number_and_no_unit_stops_the_scour_pass_like_the_python_does()
    {
        var svg = $"<svg {Ns} width=\"1.2.3\" height=\"4\"><rect width=\"1\" height=\"1\"/></svg>";

        Assert.Throws<InvalidOperationException>(() => SvgSimplifier.Simplify(svg, useGeometric: false));
    }

    [Fact]
    public void A_number_too_large_for_a_double_leaves_the_document_to_the_later_passes()
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 24 24\"><rect width=\"1e999\" height=\"1\"/></svg>";

        var result = SvgSimplifier.Simplify(svg, useGeometric: false);

        Assert.Contains("1e999", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_viewbox_part_that_is_not_a_number_is_an_error_not_a_fallback()
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 x 4\"><path d=\"M 1 2\"/></svg>";

        Assert.Throws<FormatException>(() => SvgSimplifier.Quantize(svg));
    }

    [Theory]
    [InlineData("0 0 32 32", 3)]
    [InlineData("0 0 33 20", 2)]
    [InlineData("0 0 128 128", 2)]
    [InlineData("0 0 129 5", 1)]
    public void The_viewbox_size_picks_the_precision(string viewBox, int expected)
    {
        var svg = $"<svg {Ns} viewBox=\"{viewBox}\"><path d=\"M 1.23456 2.34567\"/></svg>";

        var result = SvgSimplifier.Quantize(svg);

        var decimals = Path(result).Split(' ')[1].Split('.').ElementAtOrDefault(1)?.Length ?? 0;
        Assert.Equal(expected, decimals);
    }

    [Fact]
    public void Width_and_height_with_units_are_read_for_the_precision_and_unparsable_ones_default_to_1000()
    {
        Assert.Equal("M 1.2 2.3", Path(SvgSimplifier.Quantize($"<svg {Ns} width=\"200px\" height=\"200px\"><path d=\"M 1.234 2.345\"/></svg>")));
        Assert.Equal("M 1.2 2.3", Path(SvgSimplifier.Quantize($"<svg {Ns} width=\"1.2.3\" height=\"4\"><path d=\"M 1.234 2.345\"/></svg>")));
        Assert.Equal("M 1.23 2.35", Path(SvgSimplifier.Quantize($"<svg {Ns} width=\"100\" height=\"100\"><path d=\"M 1.234 2.345\"/></svg>")));
    }

    [Fact]
    public void A_path_with_no_commands_is_left_as_it_was()
    {
        var result = SvgSimplifier.Quantize($"<svg {Ns} viewBox=\"0 0 24 24\"><path d=\"hello\"/><path/></svg>");

        Assert.Contains("d=\"hello\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_elements_whose_name_ends_in_path_count_as_paths()
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 24 24\"><path d=\"M 1 2 L 3 4\"/><clipPath><path d=\"M 0 0 L 1 1 L 2 2\"/></clipPath><textPath d=\"M 0 0\"/></svg>";

        Assert.Equal(5, SvgSimplifier.CountPathNodes(svg));
    }

    [Fact]
    public void A_zero_tolerance_skips_the_geometric_pass_and_a_positive_one_runs_it()
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 24 24\"><path d=\"M 0 0 L 5 0.01 L 10 0\"/></svg>";

        Assert.Equal("M 0 0 L 5 0.01 L 10 0", Path(SvgSimplifier.Simplify(svg, tolerance: 0.0, useScour: false)));
        Assert.Equal("M 0 0 L 10 0", Path(SvgSimplifier.Simplify(svg, tolerance: 0.5, useScour: false)));
    }

    [Fact]
    public void Scour_can_be_turned_off()
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 24 24\"><title>t</title><rect width=\"4\" height=\"4\"/></svg>";

        Assert.Contains("<title>", SvgSimplifier.Simplify(svg, useScour: false), StringComparison.Ordinal);
        Assert.DoesNotContain("<title>", SvgSimplifier.Simplify(svg), StringComparison.Ordinal);
    }

    private static string Path(string svg)
    {
        var start = svg.IndexOf("d=\"", StringComparison.Ordinal) + 3;
        return svg[start..svg.IndexOf('"', start)];
    }
}
