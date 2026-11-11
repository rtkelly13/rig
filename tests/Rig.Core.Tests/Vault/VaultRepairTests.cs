using System.Runtime.Versioning;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// Where <see cref="VaultRepair"/> parts from the Python on purpose: a note the Python would have crashed on, or whose
/// frontmatter cannot be edited in place, is logged and passed over. The oracle holds everything else.
/// </summary>
public sealed class VaultRepairTests : IDisposable
{
    private readonly string parent = RepoTemp.NewVaultParent("rig-repair");
    private readonly List<string> said = [];
    private readonly List<string> logged = [];

    public VaultRepairTests() => Vault = Directory.CreateDirectory(Path.Combine(parent, "Vault")).FullName;

    private string Vault { get; }

    public void Dispose() => Directory.Delete(parent, recursive: true);

    [Fact]
    public void A_note_that_cannot_be_written_is_logged_and_not_counted()
    {
        var path = Write("Areas/Health/Untyped.md", "No frontmatter\n");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Repair().BackfillTypes(dryRun: false);
            Repair().RepairMetadata(dryRun: false);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal("No frontmatter\n", File.ReadAllText(path));
        Assert.Contains("\nFixed 0 notes missing 'type'.", said);
        Assert.Contains("\nRepaired 0 notes with metadata issues.", said);
        Assert.Contains(logged, l => l.StartsWith($"Error backfilling {path}: ", StringComparison.Ordinal));
        Assert.Contains(logged, l => l.StartsWith($"Error repairing {path}: ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_social_post_whose_article_cannot_be_read_is_logged_and_passed_over()
    {
        var post = Write("Social Media Posts/Talk/Post.md", "---\ntype: social\nrelated_article: '[[Paper]]'\n---\n");
        Write("Resources/Papers/Paper.md", "---\ntype: [unclosed\n---\n");

        Repair().RepairMetadata(dryRun: false);

        Assert.Contains(logged, l => l.StartsWith($"Error repairing {post}: ", StringComparison.Ordinal));
        Assert.Contains("\nRepaired 0 notes with metadata issues.", said);
    }

    [Fact]
    public void A_note_with_json_frontmatter_is_refused_even_in_a_dry_run()
    {
        var path = Write("Areas/Health/Json.md", "{\n\"status\": \"a\"\n}\nBody\n");

        Repair().BackfillTypes(dryRun: true);

        Assert.Contains($"Error backfilling {path}: its frontmatter could not be edited line by line and read back as intended", logged);
        Assert.Contains("\nFound 0 notes missing 'type'.", said);
    }

    [Fact]
    public void A_social_link_that_cannot_be_written_is_logged()
    {
        Write("Resources/Dup/Paper.md", "---\ntype: resource\nsource: https://dup.example/x\n---\n");
        var post = Write("Social Media Posts/Dup/Post.md", "---\ntype: social\nsource: https://dup.example/x\n---\n");
        File.SetAttributes(post, FileAttributes.ReadOnly);
        try
        {
            Repair().CleanupSocialDuplicates(dryRun: false);
        }
        finally
        {
            File.SetAttributes(post, FileAttributes.Normal);
        }

        Assert.Contains(logged, l => l.StartsWith($"Error repairing {post}: ", StringComparison.Ordinal));
        Assert.DoesNotContain(said, l => l.Contains('✓', StringComparison.Ordinal));
    }

    [Fact]
    public void A_duplicate_that_cannot_be_archived_is_logged_and_left()
    {
        Write("Resources/Dup/Short.md", "---\ntype: resource\nsource: https://dup.example/x\n---\none\n");
        var longer = Write("Resources/Dup/Longer.md", "---\ntype: resource\nsource: https://dup.example/x\n---\none two three\n");
        var shorter = Path.GetFullPath(Path.Join(Vault, "Resources", "Dup", "Short.md"));
        Write("Archive/Duplicates", "a file where the folder goes\n");

        Repair().CleanupResourceDuplicates(dryRun: false);

        Assert.Contains(logged, l => l.StartsWith($"Error archiving {shorter}: ", StringComparison.Ordinal));
        Assert.True(File.Exists(shorter));
        Assert.True(File.Exists(longer));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void A_rename_that_fails_is_logged_and_skipped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A read-only folder still takes renames on Windows.");
        var path = Write("Resources/Topic/my_note.md", "---\ntype: note\n---\n");
        var folder = Path.GetDirectoryName(path)!;
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Repair().CleanupFilenames(dryRun: false);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.True(File.Exists(path));
        Assert.Contains(logged, l => l.StartsWith($"Error renaming {path}: ", StringComparison.Ordinal));
        Assert.Contains("Skipped 1 filename issues due to conflicts or missing files.", said);
    }

    [Theory]
    [InlineData("one two  three", 3)]
    [InlineData("caf\u00e9 na\u0308ive x_y", 4)]
    [InlineData("a-b c.d \u00bd \u2160", 6)]
    [InlineData("\U00010400\U00010401 --- ", 1)]
    [InlineData("", 0)]
    public void Words_counts_what_pythons_w_plus_matches(string text, int expected)
    {
        Assert.Equal(expected, VaultRepair.Words(text));
    }

    [Fact]
    public void Links_to_a_renamed_note_follow_it_in_the_form_they_were_written()
    {
        var from = NotePath.Parse("Resources/Topic/my_note.md");
        var to = NotePath.Parse("Resources/Topic/my-note.md");

        var rewritten = VaultRepair.RewriteWikilinks(
            "[[my_note]] [[my_note.md]] [[Resources/Topic/my_note]] ![[Resources/Topic/my_note.md|Alias]] [[ my_note ]] [[other]] [[my_note#H]]",
            from,
            to);

        Assert.Equal("[[my-note]] [[my-note.md]] [[Resources/Topic/my-note]] ![[Resources/Topic/my-note.md|Alias]] [[my-note]] [[other]] [[my_note#H]]", rewritten);
    }

    private VaultRepair Repair() => new(Vault, said.Add, logged.Add);

    private string Write(string relative, string text)
    {
        var full = Path.GetFullPath(Path.Join(Vault, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }
}
