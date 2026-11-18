using System.Net;
using System.Text.RegularExpressions;

namespace Rig.Image.Gallery;

/// <summary>
/// The gallery's HTML fragments, embedded from <c>Gallery/templates/</c>, and the substitution that fills them. A <c>{{name}}</c> placeholder
/// is HTML-escaped; one ending in <c>_html</c> is inserted as it is, for fragments already rendered. A placeholder with no value is an error.
/// </summary>
internal static partial class GalleryTemplates
{
    /// <summary>The compiled stylesheet, inlined so the page opens straight from disk.</summary>
    public static string Css => Read("gallery.css");

    /// <summary>Fills the fragment <paramref name="name"/> (a file name under <c>templates/</c>) from <paramref name="values"/>.</summary>
    public static string Render(string name, IReadOnlyDictionary<string, string> values) => Fill(name, Read("templates." + name), values);

    /// <summary>Fills <paramref name="template"/>, which was read from <paramref name="source"/>, from <paramref name="values"/>.</summary>
    public static string Fill(string source, string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
            {
                throw new InvalidOperationException($"expected a value for {{{{{key}}}}} in {source}, found none; supplied: {string.Join(", ", values.Keys.Order(StringComparer.Ordinal))}");
            }

            return key.EndsWith("_html", StringComparison.Ordinal) ? value : WebUtility.HtmlEncode(value);
        });

    private static string Read(string file)
    {
        var name = $"Rig.Image.gallery.{file}";
        using var stream = typeof(GalleryTemplates).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded in {typeof(GalleryTemplates).Assembly.GetName().Name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();
}
