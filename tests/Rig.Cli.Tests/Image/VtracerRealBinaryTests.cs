using Rig.Cli.Image;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Image.Pathways;

namespace Rig.Cli.Tests.Image;

/// <summary>The real pinned VTracer 0.6.4, downloaded and checksum-verified, against the candidates the Python binding produced.</summary>
public sealed class VtracerRealBinaryTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("rtkelly-labs", "rtkelly-labs")]
    [InlineData("parquet-source-generator", "parquet-diamond")]
    public async Task The_cli_reproduces_the_python_bindings_autotrace(string project, string reference)
    {
        var tool = new VtracerTool(new SystemProc(), _temp.Path, VtracerTool.Fetch);
        var root = RepoRoot();
        string traced;
        try
        {
            traced = await tool.TraceAsync(Path.Join(root, "assets", project, "reference", $"{reference}.png"), new VtracerConfig(), TestContext.Current.CancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Assert.Skip($"the VTracer release could not be downloaded: {exception.Message}");
            return;
        }

        var frozen = await File.ReadAllTextAsync(Path.Join(root, "tests", "Rig.Image.Tests", "Oracle", "candidates", $"{project}.autotraced.svg"), TestContext.Current.CancellationToken);
        Assert.Equal(frozen, traced);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "rig.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("expected rig.slnx above the test binaries, found none");
    }
}
