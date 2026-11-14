using System.Text;
using Rig.Core.ReviewQueue;

namespace Rig.Core.Tests.ReviewQueue;

public sealed class QueueProjectionTests : IDisposable
{
    private readonly TempDir _temp = new();
    private string Queue => Path.Join(_temp.Path, "nested", "queue.md");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Replaces_the_block_preserves_notes_and_does_not_touch_identical_output()
    {
        var item = Item();
        QueueEntry[] entries = [new(item, [])];
        QueueProjection.Export(Queue, entries);
        var content = "Process notes\n" + File.ReadAllText(Queue) + "\nPersonal notes\n";
        File.WriteAllText(Queue, content);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Queue, stamp);
        QueueProjection.Export(Queue, entries);
        Assert.Equal(content, File.ReadAllText(Queue));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Queue));
        QueueProjection.Export(Queue, [new(item with { Title = "new title" }, [])]);
        var updated = File.ReadAllText(Queue);
        Assert.StartsWith("Process notes\n", updated, StringComparison.Ordinal);
        Assert.EndsWith("\nPersonal notes\n", updated, StringComparison.Ordinal);
        Assert.Contains("new title", updated, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(Queue, ".md.pending")));
        QueueMarkdown.Verify(updated);
    }

    [Fact]
    public void Invalid_or_unreadable_existing_documents_are_never_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Queue)!);
        File.WriteAllText(Queue, "Human notes with no generated block");
        Assert.Throws<FormatException>(() => QueueProjection.Export(Queue, []));
        Assert.Equal("Human notes with no generated block", File.ReadAllText(Queue));
        File.WriteAllBytes(Queue, [0xff]);
        Assert.Throws<DecoderFallbackException>(() => QueueProjection.Export(Queue, []));
        Assert.Equal(new byte[] { 0xff }, File.ReadAllBytes(Queue));
    }

    [Fact]
    public void Export_filters_closed_out_of_scope_deferred_and_archived_items()
    {
        var item = Item();
        QueueProjection.Export(Queue,
        [
            new(item, []),
            new(item with { Number = 2, State = "CLOSED" }, []),
            new(item with { Number = 3, InScope = false }, []),
            new(item with { Number = 4 }, [Event("deferred")]),
            new(item with { Number = 5 }, [Event("archived")]),
        ]);
        var text = File.ReadAllText(Queue);
        Assert.Contains("owner/repo#1", text, StringComparison.Ordinal);
        foreach (var number in new[] { 2, 3, 4, 5 })
        {
            Assert.DoesNotContain($"owner/repo#{number}", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_failed_replace_removes_only_the_pending_file()
    {
        Directory.CreateDirectory(Queue);
        var error = Record.Exception(() => QueueProjection.Export(Queue, [new(Item(), [])]));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(Queue));
        Assert.False(File.Exists(Path.ChangeExtension(Queue, ".md.pending")));
    }

    [Fact]
    public void Reads_normalize_crlf_and_cr_without_treating_unicode_separators_as_newlines()
    {
        var path = _temp.File("old.md");
        File.WriteAllText(path, "a\r\nb\rc\n\u2028");
        Assert.Equal("a\nb\nc\n\u2028", QueueProjection.Read(path));
    }

    private static QueueItem Item() => new("owner/repo", 1, "pr", "Change", "OPEN", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", new string('a', 40));
    private static QueueEvent Event(string action) => new(1, "2026-01-01T00:00:00Z", action, new string('a', 40), "", "local");
}
