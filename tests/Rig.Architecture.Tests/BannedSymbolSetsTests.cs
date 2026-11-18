using System.Diagnostics;

namespace Rig.Architecture.Tests;

/// <summary>
/// <c>Directory.Build.targets</c> expands each project's <c>BannedSymbolSets</c> into BannedSymbols.&lt;Set&gt;.txt files.
/// A set with no file must fail the build, never be skipped, or a typo would silently drop a ban. These run just the
/// verifying target of a throwaway project that imports the real targets file, so no compile is involved.
/// </summary>
public sealed class BannedSymbolSetsTests
{
    [Fact]
    public async Task A_misspelt_set_fails_the_build_naming_the_set_and_the_project()
    {
        var (exitCode, output) = await VerifyAsync("Console;Consol");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("RIGBAN001", output, StringComparison.Ordinal);
        Assert.Contains("'Consol'", output, StringComparison.Ordinal);
        Assert.Contains("MisspeltSets", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_set_with_a_file_passes()
    {
        var (exitCode, output) = await VerifyAsync("Console;CommandLine");

        Assert.True(exitCode == 0, output);
    }

    [Fact]
    public async Task A_project_naming_no_sets_passes()
    {
        var (exitCode, output) = await VerifyAsync(string.Empty);

        Assert.True(exitCode == 0, output);
    }

    private static async Task<(int ExitCode, string Output)> VerifyAsync(string sets)
    {
        var root = RepoRoot();
        var folder = Directory.CreateTempSubdirectory("rig-banned-sets-").FullName;
        try
        {
            var project = Path.Join(folder, "MisspeltSets.proj");
            await File.WriteAllTextAsync(
                project,
                $"""
                <Project>
                  <PropertyGroup><BannedSymbolSets>{sets}</BannedSymbolSets></PropertyGroup>
                  <Import Project="{Path.Join(root, "Directory.Build.targets")}" />
                </Project>
                """);

            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "msbuild", project, "-t:RigVerifyBannedSymbolSets", "-nologo", "-v:minimal" },
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Join(folder.FullName, "rig.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("rig.slnx is not above the test output folder.");
    }
}
