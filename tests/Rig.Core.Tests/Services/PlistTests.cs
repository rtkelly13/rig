using System.Text;
using Rig.Core.Services;

namespace Rig.Core.Tests.Services;

/// <summary>The corners of <see cref="Plist"/> the knowledge server's recorded plists do not reach.</summary>
public sealed class PlistTests
{
    [Fact]
    public void Keys_sort_and_escape_carriage_returns_fold_and_false_is_written()
    {
        var text = Encoding.UTF8.GetString(Plist.Render(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["b"] = false,
            ["A&"] = "one\r\ntwo\rthree\tfour",
        }));

        Assert.Equal(
            "<dict>\n\t<key>A&amp;</key>\n\t<string>one\ntwo\nthree\tfour</string>\n\t<key>b</key>\n\t<false/>\n</dict>\n</plist>\n",
            text[text.IndexOf("<dict>", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void A_string_dictionary_nests_with_its_keys_sorted()
    {
        var text = Encoding.UTF8.GetString(Plist.Render(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Env"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["Z"] = "1", ["A<"] = "2" },
        }));

        Assert.Equal(
            "<dict>\n\t<key>Env</key>\n\t<dict>\n\t\t<key>A&lt;</key>\n\t\t<string>2</string>\n\t\t<key>Z</key>\n\t\t<string>1</string>\n\t</dict>\n</dict>\n</plist>\n",
            text[text.IndexOf("<dict>", StringComparison.Ordinal)..]);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("a\u001fb")]
    [InlineData("\u000b")]
    public void A_control_character_is_refused(string value)
    {
        Assert.Throws<ArgumentException>(() => Plist.Render(new Dictionary<string, object> { ["k"] = value }));
    }

    [Fact]
    public void A_value_of_another_kind_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Plist.Render(new Dictionary<string, object> { ["k"] = 1.5 }));
    }
}
