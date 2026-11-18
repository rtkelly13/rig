using Rig.Image.Simplifier;

namespace Rig.Image.Tests;

/// <summary>Inputs the Python scour crashes on or leaves alone: these have no frozen output, so they are checked directly.</summary>
public sealed class ScourPassTests
{
    private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    private static string Scour(string body) => SvgSimplifier.RunScour($"<svg {Ns} viewBox=\"0 0 24 24\">{body}</svg>");

    [Theory]
    [InlineData("M 0 0 A 1 1 0 0")]
    [InlineData("M 0 0 L")]
    [InlineData("M")]
    [InlineData("L 1 1")]
    [InlineData("M 0 0 C 1 2 3")]
    [InlineData("M 0 0 A 1 1 0 2 1 5 5")]
    public void Malformed_path_data_is_survived_without_an_exception(string d)
    {
        var result = Scour($"<path d=\"{d}\"/>");

        Assert.Contains("<svg", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_translation_with_one_number_does_not_crash_the_transform_coalescing()
    {
        var result = Scour("<g transform=\"translate(5) translate(3)\"><rect width=\"1\" height=\"1\"/></g>");

        Assert.Contains("translate(8)", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("translate(1 1) translate(-1 -1) rotate(30)")]
    [InlineData("translate(1 1) translate(-1 -1) scale(2)")]
    public void An_identity_translation_at_the_front_follows_Pythons_negative_indexing_instead_of_throwing(string transform)
    {
        // Python indexes transform[-1] after removing the identity, which merges the last entry into itself and drops it;
        // the oracle pins that output, so the port must neither throw nor keep the transform.
        var result = Scour($"<g transform=\"{transform}\"><rect width=\"1\" height=\"1\"/></g>");

        Assert.DoesNotContain("transform", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Opposite_one_number_translations_cancel_out()
    {
        var result = Scour("<g transform=\"translate(5) translate(-5)\"><rect width=\"1\" height=\"1\"/></g>");

        Assert.DoesNotContain("transform", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("translate(1 2")]
    [InlineData("wobble(1)")]
    [InlineData("translate")]
    [InlineData("translate(a)")]
    [InlineData("rotate(1 2)")]
    public void A_transform_that_does_not_parse_leaves_the_document_untouched(string transform)
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 24 24\"><g transform=\"{transform}\"><rect width=\"1\" height=\"1\"/></g></svg>";

        Assert.Equal(svg, SvgSimplifier.RunScour(svg));
    }

    [Fact]
    public void A_gradient_that_folds_into_a_reference_with_no_href_to_remove_leaves_the_document_untouched()
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 24 24\"><defs><linearGradient id=\"a\"><stop offset=\"0\" stop-color=\"#f00\"/></linearGradient>"
            + "<linearGradient id=\"b\" fill=\"url(#a)\"/></defs><rect fill=\"url(#b)\" width=\"4\" height=\"4\"/></svg>";

        Assert.Equal(svg, SvgSimplifier.RunScour(svg));
    }

    [Fact]
    public void A_number_that_overflows_the_context_leaves_the_document_untouched()
    {
        var svg = $"<svg {Ns} viewBox=\"0 0 24 24\"><path d=\"M 1e999999999 0 L 1 1\"/></svg>";

        Assert.Equal(svg, SvgSimplifier.RunScour(svg));
    }

    [Fact]
    public void A_doctype_with_an_internal_subset_and_processing_instructions_survive_the_scour_text()
    {
        var svg = "<?xml-stylesheet href=\"a.css\"?><!DOCTYPE svg [<!ELEMENT svg ANY>]>"
            + $"<svg {Ns} viewBox=\"0 0 24 24\"><rect width=\"1\" height=\"1\"/></svg><?after x?>";

        var result = SvgSimplifier.RunScour(svg);

        Assert.StartsWith("<?xml-stylesheet href=\"a.css\"?>\n<!DOCTYPE svg [", result, StringComparison.Ordinal);
        Assert.EndsWith("<?after x?>\n", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!DOCTYPE svg [<!ENTITY a \"4\">]>", "&a;", "4", "<!DOCTYPE svg [<!ENTITY a \"4\">]>\n")]
    [InlineData("<!DOCTYPE svg [<!ENTITY a \"4\">]>", "1", "1", "<!DOCTYPE svg [<!ENTITY a \"4\">]>\n")]
    [InlineData("<!DOCTYPE svg [<!ELEMENT svg ANY>]>", "1", "1", "<!DOCTYPE svg [<!ELEMENT svg ANY>]>\n")]
    public void A_doctype_that_declares_entities_expands_them_as_the_python_scour_does(string doctype, string width, string scoured, string expectedStart)
    {
        var svg = $"{doctype}<svg {Ns} viewBox=\"0 0 24 24\"><rect width=\"{width}\" height=\"1\"/></svg>";

        var result = SvgSimplifier.RunScour(svg);

        Assert.StartsWith(expectedStart, result, StringComparison.Ordinal);
        Assert.Contains($"<rect width=\"{scoured}\" height=\"1\"/>", result, StringComparison.Ordinal);
    }
}
