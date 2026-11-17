using System.Text.RegularExpressions;
using Rig.Web.TestSupport;

namespace Rig.Web.Tests;

/// <summary>
/// Gate: rig's stylesheets and templates take every colour and size from the design system.
/// </summary>
/// <remarks>
/// Colours, type sizes and spacing come from <c>@rtkelly-labs/design-system</c> tokens, through Tailwind utilities
/// (<c>bg-surface-raised</c>, <c>text-caption</c>, <c>max-w-325</c>) or <c>var(--ds-*)</c>. A raw hex or <c>rgb()</c>
/// colour, or an arbitrary-value class such as <c>text-[0.82rem]</c>, bypasses the tokens and drifts from the design
/// system. Where a token is missing, the fix belongs upstream in rtkelly-labs/design-system, not here. Compiled output
/// (<c>gallery.css</c>, <c>app_assets/</c>, the C4 viewer's <c>c4.css</c>) and vendored files are out of scope:
/// Tailwind writes the theme's literal values into them.
/// </remarks>
public sealed partial class DesignTokensTests
{
    private static readonly (string Folder, string Extension)[] Scanned =
    [
        ("styles", ".css"),
        ("web", ".css"),
        ("web", ".tsx"),
        ("tools/image-drift/styles", ".css"),
        ("tools/image-drift/src/image_drift/templates", ".jinja"),
        ("src/rig/web_host/templates", ".html"),
        ("src/rig/web_host/templates", "studio.css"),
        ("src/Rig.Web/C4/static", ".html"),
    ];

    public static TheoryData<string, string[]> Detection => new()
    {
        { "<p class=\"text-caption text-content-muted\">", [] },
        { "color: var(--ds-accent-primary);", [] },
        { "background: color-mix(in oklab, var(--ds-accent-primary) 12%, transparent);", [] },
        { "<span>&#128204; Canonical</span>", [] },
        { "<a href=\"#add-row\">", [] },
        { "<a className=\"[&_svg]:size-3\">", [] },
        { "[data-theme=\"midnight\"] .c4-top {", [] },
        { "color: #0a0a1a;", ["#0a0a1a"] },
        { "background: rgba(34, 211, 238, 0.12);", ["rgba("] },
        { "fill: hsl(10 20% 30%);", ["hsl("] },
        { "<p class=\"text-[0.82rem] max-w-[1200px]\">", ["text-[0.82rem]", "max-w-[1200px]"] },
        { "<b class=\"hover:border-[#f59e0b]\">", ["#f59e0b", "border-[#f59e0b]"] },
        { "@apply w-[110px];", ["w-[110px]"] },
        { "<img class=\"[image-rendering:pixelated]\">", ["[image-rendering:pixelated]"] },
    };

    [Theory]
    [MemberData(nameof(Detection))]
    public void Violation_detection(string text, string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), Violations(text).Order(StringComparer.Ordinal));

    [Fact]
    public void Scope_covers_the_sources()
    {
        var names = ScannedFiles().Select(path => Path.GetRelativePath(Repo.Root, path).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);

        Assert.Subset(names, new HashSet<string>(["tools/image-drift/styles/gallery.src.css", "styles/c4.css", "web/app.src.css", "web/App.tsx"], StringComparer.Ordinal));
    }

    [Fact]
    public void Stylesheets_and_templates_use_design_system_tokens()
    {
        var found = ScannedFiles()
            .SelectMany(path => Violations(File.ReadAllText(path)).Select(hit => $"({Path.GetRelativePath(Repo.Root, path).Replace('\\', '/')}, {hit})"))
            .ToList();

        Assert.True(
            found.Count == 0,
            "Use @rtkelly-labs/design-system tokens (Tailwind utilities or var(--ds-*)), not raw colours or arbitrary values; "
            + $"a missing token goes upstream: [{string.Join(", ", found)}]");
    }

    // A hex colour, not an HTML entity (`&#128204;`) or an id fragment (`#add-row`).
    [GeneratedRegex(@"(?<![&\w])#[0-9a-fA-F]{3,8}\b(?![\w-])")]
    private static partial Regex RawHex();

    [GeneratedRegex(@"\b(?:rgba?|hsla?)\(")]
    private static partial Regex RawFunction();

    // `text-[0.82rem]`, `hover:max-w-[1200px]`, `-left-[3px]`.
    [GeneratedRegex(@"(?<![\w-])-?[a-z][\w-]*-\[[^\]\s]+\]")]
    private static partial Regex ArbitraryValue();

    // `[image-rendering:pixelated]`; variants like `[&_svg]:size-3` start with `&` and are allowed.
    [GeneratedRegex(@"(?<![\w&-])\[[a-z-]+:[^\]\s]+\]")]
    private static partial Regex ArbitraryProperty();

    /// <summary>Every raw colour or arbitrary-value class in a stylesheet or template.</summary>
    private static List<string> Violations(string text) =>
        [.. new[] { RawHex(), RawFunction(), ArbitraryValue(), ArbitraryProperty() }.SelectMany(pattern => pattern.Matches(text).Select(match => match.Value))];

    private static List<string> ScannedFiles() =>
        [
            .. Scanned
                .SelectMany(scan => Directory.Exists(Path.Join(Repo.Root, scan.Folder))
                    ? Directory.EnumerateFiles(Path.Join(Repo.Root, scan.Folder), "*", SearchOption.TopDirectoryOnly).Where(path => path.EndsWith(scan.Extension, StringComparison.Ordinal))
                    : [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
}
