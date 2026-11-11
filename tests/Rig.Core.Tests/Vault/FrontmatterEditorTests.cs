using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

public sealed class FrontmatterEditorTests
{
    [Fact]
    public void A_note_whose_frontmatter_does_not_load_is_refused()
    {
        var edit = FrontmatterEditor.Set("---\ntype: [unclosed\n---\n", [new("type", "note")]);

        Assert.Null(edit.Text);
        Assert.StartsWith("Invalid frontmatter: ", edit.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_set_key_and_its_continuation_lines_change()
    {
        var edit = FrontmatterEditor.Set("---\n# kept\ntags:\n- a\n\n- b\nnext: 1   # kept\n---\nBody\n", [new("tags", "x")]);

        Assert.Equal("---\n# kept\ntags: x\nnext: 1   # kept\n---\nBody\n", edit.Text);
    }

    [Fact]
    public void A_key_the_block_lacks_is_appended_to_it()
    {
        var edit = FrontmatterEditor.Set("---\ntype: note\n---\nBody\n", [new("status", "active")]);

        Assert.Equal("---\ntype: note\nstatus: active\n---\nBody\n", edit.Text);
    }

    [Fact]
    public void A_note_without_frontmatter_gains_a_block_holding_each_keys_last_value()
    {
        var edit = FrontmatterEditor.Set("Body\n", [new("type", "note"), new("status", "a"), new("type", "area")]);

        Assert.Equal("---\ntype: area\nstatus: a\n---\n\nBody\n", edit.Text);
    }

    [Fact]
    public void Json_frontmatter_is_refused_rather_than_shadowed_by_a_new_yaml_block()
    {
        var edit = FrontmatterEditor.Set("{\n\"type\": \"a\"\n}\nBody\n", [new("type", "b")]);

        Assert.Null(edit.Text);
        Assert.Equal("its frontmatter could not be edited line by line and read back as intended", edit.Refusal);
    }

    [Theory]
    [InlineData("type", "resource", "resource")]
    [InlineData("type", "yes", "\"yes\"")]
    [InlineData("type", "2024-01-01", "\"2024-01-01\"")]
    [InlineData("type", "a: b", "\"a: b\"")]
    [InlineData("type", "", "\"\"")]
    [InlineData("source", "https://example.org/a", "\"https://example.org/a\"")]
    [InlineData("type", "say \"hi\" \\ now", "say \"hi\" \\ now")]
    [InlineData("type", "\"hi\" \\ now", "\"\\\"hi\\\" \\\\ now\"")]
    [InlineData("type", "\ttab", "\"\\u0009tab\"")]
    [InlineData("type", "x\u2028y\u0085z\ufeff", "\"x\\u2028y\\u0085z\\uFEFF\"")]
    [InlineData("type", "caf\u00e9 \U0001F600", "caf\u00e9 \U0001F600")]
    public void A_value_is_plain_only_when_pyyaml_reads_it_back_as_the_same_string(string key, string value, string expected)
    {
        Assert.Equal(expected, FrontmatterEditor.Scalar(key, value));
    }

    [Theory]
    [InlineData("type", "x\u2028y\u0085z\ufeff\u007f\u009f")]
    [InlineData("type", "\"'\\ #: - [x]")]
    [InlineData("source", "https://odd.example/a#b:c'd\"e")]
    public void A_quoted_value_reads_back_as_set(string key, string value)
    {
        var edit = FrontmatterEditor.Set("---\ntype: note\n---\n", [new(key, value)]);

        Assert.NotNull(edit.Text);
        Assert.Equal(value, Frontmatter.Parse(edit.Text).Text(key));
    }

    [Fact]
    public void Metadata_differing_in_a_values_kind_is_not_the_same()
    {
        Dictionary<string, PyValue> left = new() { ["a"] = new PyStr("x") };
        Dictionary<string, PyValue> right = new() { ["a"] = PyNone.Value };

        Assert.False(FrontmatterEditor.SameMetadata(left, right));
    }
}
