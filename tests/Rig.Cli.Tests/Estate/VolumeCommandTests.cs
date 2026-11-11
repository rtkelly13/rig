using System.CommandLine;
using Rig.Cli.Estate;
using Rig.Core.Estate;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Cli.Tests.Estate;

/// <summary>
/// <c>rig estate volume</c> over a synthetic WizTree export in a temp folder, with the drive's space and the environment
/// faked: no drive is scanned and WizTree is never run. The JSON is what the Python command printed for the same export.
/// </summary>
public sealed class VolumeCommandTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;

    private static readonly Dictionary<string, string> Environment = new(StringComparer.Ordinal)
    {
        ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
        ["USERPROFILE"] = @"C:\Users\me",
        ["APPDATA"] = @"C:\Users\me\AppData\Roaming",
    };

    private static readonly (string Path, long Allocated, long Size)[] Rows =
    [
        (@"C:\", 900 * GB, 950 * GB),
        (@"C:\Users\", 500 * GB, 520 * GB),
        (@"C:\Users\me\", 490 * GB, 500 * GB),
        (@"C:\Users\me\AppData\", 300 * GB, 310 * GB),
        (@"C:\Users\me\AppData\Local\", 290 * GB, 300 * GB),
        (@"C:\Users\me\AppData\Local\Packages\", 60 * GB, 60 * GB),
        (@"C:\Users\me\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\", 55 * GB, 55 * GB),
        (@"C:\Users\me\AppData\Local\uv\", 40 * GB, 90 * GB),
        (@"C:\Users\me\AppData\Local\uv\cache\", 40 * GB, 90 * GB),
        (@"C:\Users\me\AppData\Local\Temp\", 7 * GB, 7 * GB),
        (@"C:\Users\me\.nuget\", 6 * GB, 6 * GB),
        (@"C:\Users\me\.nuget\packages\", 6 * GB, 6 * GB),
        (@"C:\Users\me\Downloads\", 20 * GB, 20 * GB),
        (@"C:\Users\me\Downloads\a, b\", 12 * GB, 12 * GB),
        (@"C:\Users\me\Café\", 8 * GB, 8 * GB),
        (@"C:\code\", 150 * GB, 200 * GB),
        (@"C:\code\personal\", 140 * GB, 190 * GB),
        (@"C:\code\personal\rig\", 80 * GB, 120 * GB),
        (@"C:\code\personal\rig\temp\", 40 * GB, 60 * GB),
        (@"C:\code\personal\rig\temp\wt\node_modules\", 10 * GB, 10 * GB),
        (@"C:\code\personal\rig\temp-old\", 2 * GB, 2 * GB),
        (@"C:\code\personal\rig\.venv\", 5 * GB, 15 * GB),
        (@"C:\code\personal\rig\.git\", 3 * GB, 3 * GB),
        (@"C:\code\personal\rig\src\bin\", 9 * GB, 9 * GB),
        (@"C:\code\personal\blog\node_modules\", 11 * GB, 11 * GB),
        (@"C:\Windows\", 380 * GB, 400 * GB),
        (@"C:\Windows\WinSxS\", 15 * GB, 30 * GB),
        (@"C:\Windows\Temp\", 1, 1),
        (@"C:\Program Files\App\bin\", 5 * GB, 5 * GB),
    ];

    // What `estate volume --csv export.csv --code-root C:\code --json` printed from Python for the same export, drive and environment.
    private const string PythonJson = """
        {
          "drive": "C",
          "total": 1073741824000,
          "free": 214748364800,
          "tree": [
            {
              "level": 0,
              "path": "C:",
              "allocated": 966367641600,
              "size": 1020054732800
            },
            {
              "level": 1,
              "path": "C:\\Users",
              "allocated": 536870912000,
              "size": 558345748480
            },
            {
              "level": 2,
              "path": "C:\\Users\\me",
              "allocated": 526133493760,
              "size": 536870912000
            },
            {
              "level": 3,
              "path": "C:\\Users\\me\\AppData",
              "allocated": 322122547200,
              "size": 332859965440
            },
            {
              "level": 4,
              "path": "C:\\Users\\me\\AppData\\Local",
              "allocated": 311385128960,
              "size": 322122547200
            },
            {
              "level": 3,
              "path": "C:\\Users\\me\\Downloads",
              "allocated": 21474836480,
              "size": 21474836480
            },
            {
              "level": 4,
              "path": "C:\\Users\\me\\Downloads\\a, b",
              "allocated": 12884901888,
              "size": 12884901888
            },
            {
              "level": 3,
              "path": "C:\\Users\\me\\Caf\u00e9",
              "allocated": 8589934592,
              "size": 8589934592
            },
            {
              "level": 3,
              "path": "C:\\Users\\me\\.nuget",
              "allocated": 6442450944,
              "size": 6442450944
            },
            {
              "level": 4,
              "path": "C:\\Users\\me\\.nuget\\packages",
              "allocated": 6442450944,
              "size": 6442450944
            },
            {
              "level": 1,
              "path": "C:\\Windows",
              "allocated": 408021893120,
              "size": 429496729600
            },
            {
              "level": 2,
              "path": "C:\\Windows\\WinSxS",
              "allocated": 16106127360,
              "size": 32212254720
            },
            {
              "level": 1,
              "path": "C:\\code",
              "allocated": 161061273600,
              "size": 214748364800
            },
            {
              "level": 2,
              "path": "C:\\code\\personal",
              "allocated": 150323855360,
              "size": 204010946560
            },
            {
              "level": 3,
              "path": "C:\\code\\personal\\rig",
              "allocated": 85899345920,
              "size": 128849018880
            },
            {
              "level": 4,
              "path": "C:\\code\\personal\\rig\\temp",
              "allocated": 42949672960,
              "size": 64424509440
            },
            {
              "level": 4,
              "path": "C:\\code\\personal\\rig\\.venv",
              "allocated": 5368709120,
              "size": 16106127360
            }
          ],
          "code_root": "C:\\code",
          "partition": {
            "source + everything else (code root)": 88046829568,
            "scratch in temp/": 42949672960,
            "node_modules": 11811160064,
            "build output": 9663676416,
            "python venv": 5368709120,
            ".git object stores": 3221225472
          },
          "hogs": [
            {
              "label": "WSL distro disks",
              "allocated": 59055800320,
              "paths": [
                "C:\\Users\\me\\AppData\\Local\\Packages\\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc"
              ],
              "remedy": "wsl --shutdown; wsl --manage <distro> --set-sparse true",
              "safety": "review"
            },
            {
              "label": "uv cache",
              "allocated": 42949672960,
              "paths": [
                "C:\\Users\\me\\AppData\\Local\\uv\\cache"
              ],
              "remedy": "uv cache prune (only frees entries no .venv links to)",
              "safety": "safe"
            },
            {
              "label": "Downloads",
              "allocated": 21474836480,
              "paths": [
                "C:\\Users\\me\\Downloads"
              ],
              "remedy": "estate downloads",
              "safety": "review"
            },
            {
              "label": "Component store",
              "allocated": 16106127360,
              "paths": [
                "C:\\Windows\\WinSxS"
              ],
              "remedy": "Dism /online /Cleanup-Image /StartComponentCleanup",
              "safety": "never"
            },
            {
              "label": "User temp",
              "allocated": 7516192768,
              "paths": [
                "C:\\Users\\me\\AppData\\Local\\Temp"
              ],
              "remedy": "delete entries older than a day",
              "safety": "safe"
            },
            {
              "label": "NuGet global packages",
              "allocated": 6442450944,
              "paths": [
                "C:\\Users\\me\\.nuget\\packages"
              ],
              "remedy": "dotnet nuget locals all --clear",
              "safety": "safe"
            }
          ],
          "root_files": 0
        }
        """;

    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Export()
    {
        var lines = new List<string>
        {
            "\ufeffGenerated by WizTree 4.32 (You can hide this message by making a donation)",
            "File Name,Size,Allocated,Modified,Attributes,Files,Folders,DRIVECAPACITY,FREESPACE,USEDSPACE,RESERVEDSPACE",
        };
        foreach (var (row, index) in Rows.Select((row, index) => (row, index)))
        {
            var quoted = "\"" + row.Path.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            lines.Add(index == 0
                ? $"{quoted},{row.Size},{row.Allocated},2026/10/08 10:00:00,0,10,3,2000,100,1900,0"
                : $"{quoted},{row.Size},{row.Allocated},2026/10/08 10:00:00,0,4,1");
        }

        var csv = Path.Join(_root.Path, "export.csv");
        File.WriteAllText(csv, string.Join("\n", lines) + "\n");
        return csv;
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var command = new VolumeCommand(
            new EstateHost(_root.Path, Path.Join(_root.Path, "home"), _root.Path),
            new WizTreeExport(new FakeProc(), clock, clock.SleepAsync, Path.Join(_root.Path, "absent", "WizTree64.exe")),
            name => Environment.GetValueOrDefault(name),
            _ => new DriveSpace(1000 * GB, 200 * GB),
            output,
            error);
        var code = await new RootCommand { command.Build() }.Parse(["volume", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_json_is_what_python_printed_for_the_same_export()
    {
        var (code, output, error) = await RunAsync("--csv", Export(), "--code-root", @"C:\code", "--json");

        Assert.Equal((0, string.Empty), (code, error));
        Assert.Equal(PythonJson.ReplaceLineEndings("\n") + "\n", output);
    }

    [Fact]
    public async Task The_tables_drill_down_partition_the_code_root_and_list_the_hogs()
    {
        var (code, output, _) = await RunAsync("--csv", Export(), "--code-root", @"C:\code");

        Assert.Equal(0, code);
        var lines = output.Split('\n');
        Assert.Equal(["C: 800.0GB used of 1000.0GB · 200.0GB free (20.0%)", string.Empty, "Largest folders (>= 5GB)"], lines[..3]);
        Assert.Matches(@"^Folder\s+Allocated\s+Logical$", lines[3]);
        Assert.Matches(@"^C:\s+900\.0GB\s+950\.0GB$", lines[4]);
        Assert.Matches(@"^  Users\s+500\.0GB\s+520\.0GB$", lines[5]);
        Assert.Matches(@"^        Local\s+290\.0GB\s+300\.0GB$", lines[8]);
        Assert.Contains(lines, line => line.StartsWith(@"Dev artefacts under C:\code (outermost match, no overlap)", StringComparison.Ordinal));
        Assert.Contains(lines, line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^scratch in temp/\s+40\.0GB$"));
        Assert.Contains(lines, line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^WSL distro disks\s+55\.0GB\s+wsl --shutdown; wsl --manage <distro> --set-sparse true\s+review$"));
        Assert.DoesNotContain(lines, line => line.StartsWith("Windows temp", StringComparison.Ordinal) || line.StartsWith("Files at drive root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Files_at_the_drive_root_get_a_row_of_their_own()
    {
        var csv = Path.Join(_root.Path, "root.csv");
        await File.WriteAllTextAsync(csv, "banner\nheader\n\"C:\\\",10,2147483648,x,0,1\n\"C:\\Users\\\",10,1073741824,x,0,1\n", Token);

        var (code, output, _) = await RunAsync("--csv", csv, "--min", "1GB");

        Assert.Equal(0, code);
        Assert.Matches(@"(?m)^Files at drive root\s+1\.0GB\s+powercfg /h off frees hiberfil\.sys\s+review$", output);
    }

    [Fact]
    public async Task An_unreadable_export_fails_with_its_reason()
    {
        var (code, output, error) = await RunAsync("--csv", Path.Join(_root.Path, "missing.csv"));

        Assert.Equal((1, string.Empty), (code, output));
        Assert.Contains("missing.csv", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_export_it_runs_wiztree_and_says_so_when_wiztree_is_missing()
    {
        var (code, output, error) = await RunAsync("--drive", "D");

        Assert.Equal((1, string.Empty), (code, output));
        var lines = error.Split('\n');
        Assert.StartsWith("scanning D: with WizTree (approve the UAC prompt) -> ", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("wiztree-d.csv", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("WizTree not found at ", lines[1], StringComparison.Ordinal);
    }
}
