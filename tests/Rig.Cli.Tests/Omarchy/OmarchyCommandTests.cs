using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Omarchy;
using Rig.Core.Omarchy;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Text;

namespace Rig.Cli.Tests.Omarchy;

/// <summary><c>rig omarchy doctor</c> and <c>rig omarchy sync</c> over a temp home and a fake <c>omarchy</c> CLI.</summary>
public sealed class OmarchyCommandTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private OmarchyPaths Paths => new(
        _root.Path,
        Path.Join(_root.Path, "config"),
        Path.Join(_root.Path, "state"),
        Path.Join(_root.Path, "etc"),
        Path.Join(_root.Path, "omarchy"));

    private string PolicyFile => Path.Join(_root.Path, "rig", OmarchyPolicy.RelativePath);

    private void Installed() => _ = Directory.CreateDirectory(Paths.InstallRoot);

    private void Policy(string text)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(PolicyFile)!);
        File.WriteAllText(PolicyFile, text);
    }

    private void Shell(string text)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Paths.ShellFile)!);
        File.WriteAllText(Paths.ShellFile, text);
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new OmarchyCommand(new OmarchyHost(Paths, PolicyFile, error), _proc, output);
        string[] full = ["omarchy", .. args];
        var code = await new RootCommand { command.Build() }
            .Parse(full)
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Doctor_lists_the_drift_and_exits_1()
    {
        Installed();
        Shell("""{"version": 1, "idle": {"screensaver": 150, "lock": 300}}""");
        Policy("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");

        var (code, output, _) = await RunAsync("doctor");

        Assert.Equal(1, code);
        Assert.Contains("Omarchy vs config/omarchy.json", output, StringComparison.Ordinal);
        Assert.Contains("screensaver", output, StringComparison.Ordinal);
        Assert.Contains("fail", output, StringComparison.Ordinal);
        Assert.Contains("rig omarchy sync", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_json_reports_each_setting_with_its_fix()
    {
        Installed();
        Shell("""{"version": 1, "idle": {"screensaver": 150, "lock": 300}}""");
        Policy("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");

        var (code, output, _) = await RunAsync("doctor", "--json");

        Assert.Equal(1, code);
        var rows = JsonNode.Parse(output)!.AsArray().Select(row => row!.AsObject()).ToList();
        var screensaver = Assert.Single(rows, row => (string?)row["setting"] == "screensaver");
        Assert.Equal(("idle", "150", "0", "fail"), ((string?)screensaver["area"], (string?)screensaver["have"], (string?)screensaver["want"], (string?)screensaver["status"]));
        Assert.Contains("Settings", (string?)screensaver["fix"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_exits_0_when_the_machine_matches()
    {
        Installed();
        Shell("""{"version": 1, "idle": {"screensaver": 0, "lock": 0}}""");
        Policy("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");

        var (code, output, _) = await RunAsync("doctor");

        Assert.Equal(0, code);
        Assert.Contains("✓ This machine matches config/omarchy.json.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_exits_2_and_names_a_missing_policy()
    {
        var (code, output, error) = await RunAsync("doctor");

        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains(PolicyFile, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_says_so_when_Omarchy_is_not_installed()
    {
        Policy("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");

        var (code, output, _) = await RunAsync("doctor");

        Assert.Equal(1, code);
        Assert.Contains("Install Omarchy", output, StringComparison.Ordinal);
        Assert.DoesNotContain("screensaver", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_dry_run_plans_without_writing()
    {
        Installed();
        Shell("""{"version": 1, "idle": {"screensaver": 150, "lock": 300}}""");
        Policy("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");

        var (code, output, _) = await RunAsync("sync");

        Assert.Equal(0, code);
        Assert.Contains("DRY-RUN", output, StringComparison.Ordinal);
        Assert.Contains("planned", output, StringComparison.Ordinal);
        Assert.Contains("150", await File.ReadAllTextAsync(Paths.ShellFile, Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_apply_writes_the_plan()
    {
        Installed();
        Shell("""{"version": 1, "idle": {"screensaver": 150, "lock": 300}}""");
        Policy("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");

        var (code, output, _) = await RunAsync("sync", "--apply");

        Assert.Equal(0, code);
        Assert.Contains("APPLIED", output, StringComparison.Ordinal);
        var shell = JsonNode.Parse(await File.ReadAllTextAsync(Paths.ShellFile, Token))!;
        Assert.Equal(0, JsonShape.AsInt32(shell["idle"]?["screensaver"]));
        Assert.Equal(0, JsonShape.AsInt32(shell["idle"]?["lock"]));
    }

    [Fact]
    public async Task Sync_reports_nothing_to_do_as_a_clean_run()
    {
        Installed();
        Shell("""{"version": 1, "idle": {"screensaver": 0, "lock": 0}}""");
        Policy("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");

        var (code, output, _) = await RunAsync("sync");

        Assert.Equal((0, "This machine matches config/omarchy.json.\n"), (code, output));
    }

    [Fact]
    public async Task Sync_apply_exits_1_when_a_change_fails()
    {
        Installed();
        Shell("""{"version": 1}""");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Paths.ThemeNameFile)!);
        await File.WriteAllTextAsync(Paths.ThemeNameFile, "old", Token);
        _ = _proc.Answer(new Ran(1, string.Empty, "no such theme"), "omarchy", "theme", "set");
        Policy("""{"theme": "tokyo-night"}""");

        var (code, output, _) = await RunAsync("sync", "--apply", "--json");

        Assert.Equal(1, code);
        var action = Assert.Single(JsonNode.Parse(output)!.AsArray());
        Assert.Equal(("theme", "error"), ((string?)action!["area"], (string?)action["status"]));
        Assert.Contains("no such theme", (string?)action["detail"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_json_dry_run_marks_every_change_planned()
    {
        Installed();
        Shell("""{"version": 1, "idle": {"screensaver": 150, "lock": 300}}""");
        Policy("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");

        var (code, output, _) = await RunAsync("sync", "--json");

        Assert.Equal(0, code);
        Assert.All(JsonNode.Parse(output)!.AsArray(), row => Assert.Equal("planned", (string?)row!["status"]));
    }
}
