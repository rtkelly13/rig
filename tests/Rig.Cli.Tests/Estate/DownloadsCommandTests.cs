using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Estate;
using Rig.Core.Estate;
using Rig.Core.Tests;
using Rig.Core.Tests.Estate;

namespace Rig.Cli.Tests.Estate;

/// <summary>
/// <c>rig estate downloads</c> over a Downloads folder and a stand-in Google Drive in a temp folder: the real Downloads is
/// never read. Ported from the downloads cases of <c>tests/test_estate_cli.py</c>.
/// </summary>
public sealed class DownloadsCommandTests : IDisposable
{
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Folder => _root.Folder("Downloads");

    private string Drive => _root.Folder("Drive");

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        await File.WriteAllBytesAsync(Path.Join(Folder, "setup.dmg"), new byte[2048], Token);
        await File.WriteAllBytesAsync(Path.Join(Drive, "setup.dmg"), new byte[2048], Token);
        await File.WriteAllBytesAsync(Path.Join(Folder, "data.csv"), new byte[1024], Token);
        using var output = new StringWriter();
        var command = new DownloadsCommand(new Downloads(new Walked()), Folder, () => [Drive], output);
        var code = await new RootCommand { command.Build() }.Parse(["downloads", "--min", "1KB", .. args]).InvokeAsync(new InvocationConfiguration { Output = output }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_json_names_the_drive_copy()
    {
        var (code, output) = await RunAsync("--json");

        Assert.Equal(0, code);
        var expected = new JsonArray(
            new JsonObject
            {
                ["name"] = "setup.dmg",
                ["path"] = Path.Join(Folder, "setup.dmg"),
                ["bytes"] = 2048,
                ["category"] = "installer",
                ["is_dir"] = false,
                ["cloud_duplicate"] = Path.Join(Drive, "setup.dmg"),
            },
            new JsonObject
            {
                ["name"] = "data.csv",
                ["path"] = Path.Join(Folder, "data.csv"),
                ["bytes"] = 1024,
                ["category"] = "dataset",
                ["is_dir"] = false,
                ["cloud_duplicate"] = null,
            });
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(output)), output);
    }

    [Fact]
    public async Task The_table_separates_backed_up_from_unique()
    {
        var (code, output) = await RunAsync();

        Assert.Equal(0, code);
        var lines = output.Split('\n');
        Assert.Equal("Downloads Triage", lines[0]);
        Assert.Matches(@"^Item\s+Category\s+Size\s+Cloud Backup Status$", lines[1]);
        Assert.Matches(@"^setup\.dmg\s+installer\s+2\.0KB\s+duplicate in Drive \(setup\.dmg\)$", lines[2]);
        Assert.Matches(@"^data\.csv\s+dataset\s+1\.0KB\s+unique local file$", lines[3]);
        Assert.Equal(["", "3.0KB in large downloads · 2.0KB already backed up to Google Drive", ""], lines[4..]);
    }
}
