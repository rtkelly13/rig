using Rig.Web.TestSupport;

namespace Rig.Web.Tests;

/// <summary>The Asset Studio's template, stylesheet and script keep the structure the pages and the design system rely on.</summary>
public sealed class FrontendAssetsTests
{
    [Fact]
    public void Index_html_has_the_expected_structure_and_design_system_tokens()
    {
        var content = Read("index.html");

        // The midnight theme and the bracketed headers.
        Assert.Contains("data-theme=\"midnight\"", content, StringComparison.Ordinal);
        Assert.Contains("id=\"view-workbench\"", content, StringComparison.Ordinal);
        Assert.Contains("[ REFERENCE_SEQUENCE ]", content, StringComparison.Ordinal);
        Assert.Contains("[ PRODUCTION_CUTS ]", content, StringComparison.Ordinal);
        Assert.Contains("[ VARIATIONS_&_SNAPSHOTS ]", content, StringComparison.Ordinal);

        // The agent feedback console was removed for a pure visual comparison focus.
        Assert.DoesNotContain("AGENT_FEEDBACK_CONSOLE", content, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"feedback-notes\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"btn-copy-prompt\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"btn-save-feedback\"", content, StringComparison.Ordinal);

        // Linkage to the vendored design system and the custom studio styles.
        Assert.Contains("href=\"/static/app_assets/app.css\"", content, StringComparison.Ordinal);
        Assert.Contains("href=\"/static/studio.css\"", content, StringComparison.Ordinal);
        Assert.Contains("src=\"/static/app_assets/app.js\"", content, StringComparison.Ordinal);
        Assert.Contains("id=\"legacy-workspace\" hidden", content, StringComparison.Ordinal);

        // Comparison modes and controls.
        Assert.Contains("id=\"mode-side-by-side\"", content, StringComparison.Ordinal);
        Assert.Contains("id=\"mode-split-slider\"", content, StringComparison.Ordinal);
        Assert.Contains("id=\"mode-diff-heatmap\"", content, StringComparison.Ordinal);
        Assert.Contains("id=\"btn-quick-snapshot\"", content, StringComparison.Ordinal);
        Assert.Contains("id=\"studio-status\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Studio_css_follows_the_brutalist_design_system_rules()
    {
        var content = Read("studio.css");

        // Square corners.
        Assert.Contains("border-radius: 0px", content, StringComparison.Ordinal);
        Assert.DoesNotContain("border-radius: 4px", content, StringComparison.Ordinal);
        Assert.DoesNotContain("border-radius: 8px", content, StringComparison.Ordinal);

        // Role-based CSS variables.
        Assert.Contains("var(--ds-surface-base", content, StringComparison.Ordinal);
        Assert.Contains("var(--ds-accent-primary", content, StringComparison.Ordinal);
        Assert.Contains("var(--ds-font-mono", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Studio_js_defines_the_expected_endpoints_and_modes_and_drops_the_feedback_console()
    {
        var content = Read("studio.js");

        foreach (var expected in new[] { "/api/projects", "/api/diff", "/api/snapshot", "side-by-side", "split-slider", "diff-heatmap", "updateSliderPosition", "applyZoom" })
        {
            Assert.Contains(expected, content, StringComparison.Ordinal);
        }

        foreach (var removed in new[] { "buildPrompt", "dom.btnCopyPrompt", "dom.btnSaveFeedback" })
        {
            Assert.DoesNotContain(removed, content, StringComparison.Ordinal);
        }
    }

    private static string Read(string name)
    {
        var path = Path.Join(Repo.Templates, name);
        Assert.True(File.Exists(path), $"{name} must exist in the templates directory");
        return File.ReadAllText(path);
    }
}
