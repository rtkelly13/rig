using Rig.Core.Governance;

namespace Rig.Core.Tests.Governance;

/// <summary><see cref="OssTemplates"/> over temp repos. Ported from <c>tests/test_oss_templates.py</c>.</summary>
public sealed class OssTemplatesTests : IDisposable
{
    private static readonly OssTemplateContext Context = new("lake", "rtkelly-labs/lake", "maintainer@example.com");
    private readonly TempDir _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Every_template_renders_with_no_placeholder_left_and_one_trailing_newline()
    {
        foreach (var (template, _) in OssTemplates.Mappings)
        {
            var text = OssTemplates.Render(template, Context);

            Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);
            Assert.False(text.EndsWith("\n\n", StringComparison.Ordinal), template);
        }
    }

    [Fact]
    public void The_context_fills_the_contact_and_repo()
    {
        var all = string.Concat(OssTemplates.Mappings.Select(m => OssTemplates.Render(m.Template, Context)));

        Assert.Contains("maintainer@example.com", all, StringComparison.Ordinal);
        Assert.Contains("rtkelly-labs/lake", all, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_template_is_rejected() =>
        Assert.Throws<ArgumentException>(() => OssTemplates.Render("nope.jinja", Context));

    [Fact]
    public void Inspect_lists_present_and_missing_targets()
    {
        _repo.File("CONTRIBUTING.md");

        var audit = OssTemplates.Inspect(_repo.Path);

        Assert.Equal(6, audit.TotalTemplates);
        Assert.Equal(["CONTRIBUTING.md"], audit.Present);
        Assert.Equal(5, audit.Missing.Count);
        Assert.False(audit.IsFullyAligned);
    }

    [Fact]
    public void Sync_writes_the_differing_templates_once_and_a_dry_run_writes_nothing()
    {
        var dry = OssTemplates.Sync(_repo.Path, Context, dryRun: true);
        Assert.True(dry.Changed);
        Assert.Equal(OssTemplates.Mappings.Select(m => $"[Dry Run] Would write {m.Target}"), dry.Messages);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_repo.Path));

        var wrote = OssTemplates.Sync(_repo.Path, Context, dryRun: false);
        Assert.Equal(OssTemplates.Mappings.Select(m => $"Wrote {m.Target}"), wrote.Messages);
        Assert.True(OssTemplates.Inspect(_repo.Path).IsFullyAligned);

        var again = OssTemplates.Sync(_repo.Path, Context, dryRun: false);
        Assert.False(again.Changed);
        Assert.Empty(again.Messages);
    }
}
