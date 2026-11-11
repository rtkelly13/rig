using Rig.Core.Estate;

namespace Rig.Core.Tests.Estate;

/// <summary>
/// <see cref="Downloads"/> over a Downloads folder and a stand-in Google Drive in a temp folder. Ported from the downloads
/// cases of <c>tests/test_estate_system.py</c>.
/// </summary>
public sealed class DownloadsTests : IDisposable
{
    private static readonly string[] Samples = ["shoot", "tables", "misc"];

    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Folder => _root.Folder("Downloads");

    private string Cloud => _root.Folder("Cloud");

    private Task<IReadOnlyList<DownloadItem>> ScanAsync(params string[] cloudRoots) => new Downloads(new Walked()).ScanAsync(Folder, cloudRoots, 10, Token);

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("app.dmg", "installer")]
    [InlineData("data.parquet", "dataset")]
    [InlineData("photo.JPG", "media")]
    [InlineData("files.zip", "archive")]
    [InlineData("paper.pdf", "document")]
    [InlineData("mystery.bin", "other")]
    public void A_file_is_classified_by_its_extension(string name, string category) =>
        Assert.Equal(category, Downloads.Classify(_root.File(name)));

    [Fact]
    public void A_folder_is_classified_by_the_files_it_holds()
    {
        _ = _root.File("shoot/a.mov");
        _ = _root.File("shoot/b.csv");
        _ = _root.File("tables/t.tsv");
        _ = _root.File("misc/readme");

        Assert.Equal(
            ["media", "dataset", "other"],
            Samples.Select(name => Downloads.Classify(Path.Join(_root.Path, name))));
    }

    [Fact]
    public async Task A_copy_in_drive_is_found_and_a_unique_file_is_not_and_the_largest_comes_first()
    {
        var report = Write(Path.Join(Folder, "report.pdf"), string.Concat(Enumerable.Repeat("content", 100)));
        var copy = Write(Path.Join(Cloud, "report.pdf"), string.Concat(Enumerable.Repeat("content", 100)));
        _ = Write(Path.Join(Folder, "notes.txt"), string.Concat(Enumerable.Repeat("unique", 10)));
        _ = Write(Path.Join(Folder, "small.txt"), "x");
        _ = Write(Path.Join(Folder, ".hidden"), new string('h', 100));

        var items = await ScanAsync(Cloud);

        Assert.Equal(
            [("report.pdf", report, 700L, "document", copy), ("notes.txt", Path.Join(Folder, "notes.txt"), 60L, "document", null)],
            items.Select(item => (item.Name, item.Path, item.Bytes, item.Category, item.CloudDuplicate)));
    }

    [Fact]
    public async Task A_copy_must_hold_the_same_bytes_not_just_the_same_name_and_size()
    {
        _ = Write(Path.Join(Folder, "a.bin"), new string('A', 100));
        _ = Write(Path.Join(Cloud, "a.bin"), new string('B', 100));
        _ = Write(Path.Join(Folder, "set", "f.txt"), new string('1', 50));
        _ = Write(Path.Join(Cloud, "set", "f.txt"), new string('2', 50));
        _ = Write(Path.Join(Cloud, "set", ".DS_Store"), "finder");

        Assert.All(await ScanAsync(Cloud), item => Assert.Null(item.CloudDuplicate));

        _ = Write(Path.Join(Cloud, "a.bin"), new string('A', 100));
        _ = Write(Path.Join(Cloud, "set", "f.txt"), new string('1', 50));

        var items = await ScanAsync(Cloud);
        Assert.All(items, item => Assert.NotNull(item.CloudDuplicate));
        Assert.True(items.Single(item => item.Name == "set").IsDir);
    }

    [Fact]
    public void A_folder_and_a_file_of_the_same_name_are_not_copies()
    {
        var folder = _root.Folder("left/item");
        var file = Write(Path.Join(_root.Path, "right", "item"), "x");

        Assert.False(Downloads.ContentsIdentical(folder, file));
        Assert.False(Downloads.ContentsIdentical(Write(Path.Join(_root.Path, "a"), "x"), Path.Join(_root.Path, "absent")));
    }

    [Fact]
    public async Task A_broken_link_is_skipped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symlink needs developer mode on Windows.");
        _ = Directory.CreateSymbolicLink(Path.Join(Folder, "dangling"), Path.Join(_root.Path, "missing"));
        _ = Write(Path.Join(Folder, "real.bin"), new string('x', 100));

        Assert.Equal(["real.bin"], (await ScanAsync()).Select(item => item.Name));
    }

    [Fact]
    public async Task A_missing_downloads_folder_has_nothing_in_it() =>
        Assert.Empty(await new Downloads(new Walked()).ScanAsync(Path.Join(_root.Path, "absent"), [], 0, Token));

    [Fact]
    public void Only_the_drive_folders_that_exist_are_cloud_roots()
    {
        var drive = _root.Folder("Google Drive/My Drive");

        Assert.Equal([drive], Downloads.DefaultCloudRoots(_root.Path));
        Assert.Equal(Path.Join(_root.Path, "Downloads"), Downloads.DefaultFolder(_root.Path));
    }
}
