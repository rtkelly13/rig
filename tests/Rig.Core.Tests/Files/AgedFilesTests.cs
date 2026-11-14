using Rig.Core.Files;

namespace Rig.Core.Tests.Files;

/// <summary>Age-based retention shared by the collector's files and the workload samples.</summary>
public sealed class AgedFilesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Only_picked_jsonl_files_past_the_window_expire()
    {
        var folder = temp.Folder("data");
        var old = Touch(folder, "a.jsonl", days: 8);
        Touch(folder, "b.jsonl", days: 6);
        Touch(folder, "skip.jsonl", days: 30);
        Touch(folder, "c.txt", days: 30);

        bool Pick(string name) => !name.StartsWith("skip", StringComparison.Ordinal);

        Assert.Equal([old], AgedFiles.Expired(folder, Pick, Now));
        Assert.Empty(AgedFiles.Expired(folder, Pick, Now, keepDays: 9));
        Assert.Equal([old], AgedFiles.Prune(folder, Pick, Now, apply: true));
        Assert.False(File.Exists(old));
        Assert.Empty(AgedFiles.Expired(Path.Join(temp.Path, "missing"), Pick, Now));
        Assert.Throws<ArgumentNullException>(() => AgedFiles.Expired(folder, null!, Now));
    }

    private static string Touch(string folder, string name, int days)
    {
        var path = Path.Join(folder, name);
        File.WriteAllText(path, string.Empty);
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddDays(-days));
        return path;
    }
}
