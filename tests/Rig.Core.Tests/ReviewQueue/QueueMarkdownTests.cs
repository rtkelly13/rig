using System.Text.Json;
using Rig.Core.ReviewQueue;

namespace Rig.Core.Tests.ReviewQueue;

public sealed class QueueMarkdownTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public void Projection_matches_recorded_bytes()
    {
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "ReviewQueue", "queue-markdown-oracle.json")));
        foreach (var example in oracle.RootElement.EnumerateArray())
        {
            var items = example.GetProperty("items").Deserialize<QueueItem[]>(Json)!;
            var expected = example.GetProperty("markdown").GetString()!;
            Assert.Equal(expected, QueueMarkdown.Render(items));
            QueueMarkdown.Verify(expected);
        }
    }

    [Fact]
    public void Ordering_is_stable_and_matches_the_existing_queue()
    {
        QueueItem[] items = [Item(9), Item(8) with { Lines = 1 }, Item(7) with { Priority = 0, Readiness = 3 },
            Item(6) with { Created = "2025-01-01T00:00:00Z" }, Item(5), Item(4) with { Readiness = 1 }];
        Assert.Equal([7, 8, 6, 5, 9, 4], QueueOrder.Sort(items).Select(item => item.Number));
        Assert.Equal(QueueMarkdown.Render(items), QueueMarkdown.Render(items.Reverse()));
    }

    [Fact]
    public void Projection_preserves_prose_escapes_titles_and_restarts_issue_ranks()
    {
        var pr = Item(1) with { Title = "  Change | <script> & 'quotes'\n\"hello\"\x1cworld  " };
        var issue = Item(2) with { Kind = "issue", Head = "", Priority = 0 };
        var content = QueueMarkdown.Project([issue, pr]);
        Assert.Contains("Change &#124; &lt;script&gt; &amp; &#x27;quotes&#x27; &quot;hello&quot; world", content, StringComparison.Ordinal);
        Assert.Contains("| 1 | issue | [owner/repo#2](https://github.com/owner/repo/issues/2)", content, StringComparison.Ordinal);
        var notes = "My process\n\n" + content + "\nMy notes\n";
        Assert.Equal(notes, QueueMarkdown.Project([pr, issue], notes));
        QueueMarkdown.Verify(notes);
        QueueMarkdown.Verify(QueueMarkdown.Render([]));
    }

    [Theory]
    [InlineData("| 1 |", "| 7 |", "order")]
    [InlineData("P0", "P9", "priority")]
    [InlineData("/pull/1", "/pull/9", "URL")]
    [InlineData(QueueMarkdown.End, "", "begin/end")]
    [InlineData("| 1 |", "| 1 | extra |", "nine columns")]
    [InlineData("2026-01-01T00:00:00Z", "2026-01-01T00:00:00", "creation time")]
    [InlineData("clean", "unknown", "readiness")]
    [InlineData("| 10 |", "| -1 |", "size")]
    [InlineData("| 1 | pr", "| 0 | pr", "rank")]
    [InlineData("| 1 | pr", "| 1 | unknown", "kind")]
    [InlineData("[owner/repo#1]", "owner/repo#1", "reference link")]
    [InlineData(QueueMarkdown.Header, "bad header", "header")]
    public void Invalid_documents_are_refused(string before, string after, string message)
    {
        var text = QueueMarkdown.Render([Item(1) with { Priority = 0 }]).Replace(before, after, StringComparison.Ordinal);
        Assert.Contains(message, Assert.Throws<FormatException>(() => QueueMarkdown.Verify(text)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_duplicate_references_blocks_reversed_markers_and_bad_heads()
    {
        Assert.Contains("Duplicate", Assert.Throws<FormatException>(() => QueueMarkdown.Verify(QueueMarkdown.Render([Item(1), Item(1)]))).Message, StringComparison.Ordinal);
        var text = QueueMarkdown.Render([Item(1)]);
        Assert.Throws<FormatException>(() => QueueMarkdown.Verify(text + text));
        Assert.Throws<FormatException>(() => QueueMarkdown.Verify(QueueMarkdown.End + QueueMarkdown.Begin));
        Assert.Throws<FormatException>(() => QueueMarkdown.Verify(text.Replace(new string('a', 40), "bad-head", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => QueueMarkdown.Project([], "human notes without markers"));
    }

    [Fact]
    public void Equivalent_offsets_sort_by_instant_with_ordinal_repository_tiebreaks()
    {
        var first = Item(1) with { Repo = "owner/A", Created = "2026-01-01T01:00:00+01:00" };
        var second = Item(2) with { Repo = "owner/b" };
        Assert.Equal([first, second], QueueOrder.Sort([second, first]));
    }

    private static QueueItem Item(int number) => new("owner/repo", number, "pr", "Change | <script> & links", "OPEN",
        "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", new string('a', 40), Readiness: 0, Lines: 10);
}
