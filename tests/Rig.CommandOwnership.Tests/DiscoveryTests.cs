using System.Text.Json;
using Rig.CommandOwnership;
using Rig.Core.CommandSurface;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using OwnershipRules = Rig.Core.CommandSurface.CommandOwnership;

namespace Rig.CommandOwnership.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public void New_executable_projects_mains_and_file_apps_are_discovered_and_refused()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.File("src/New/New.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        File.WriteAllText(temp.File("src/New/Entry.cs"), "class Entry { static int Main(string[] args) => 0; }");
        File.WriteAllText(temp.File("scripts/new.cs"), "#:property PublishAot=false\nSystem.Console.WriteLine(1);");
        var rows = Discovery.Sources(temp.Path, ["src/New/New.csproj", "src/New/Entry.cs", "scripts/new.cs"]).ToArray();
        Assert.Equal(3, rows.Length);
        var errors = OwnershipRules.Check(rows.Select(r => r.Id).ToArray(), new OwnershipLedger([], [], [], []));
        Assert.Equal(3, errors.Length);
        Assert.All(errors, error => Assert.StartsWith("Unregistered executable surface", error, StringComparison.Ordinal));
    }

    [Fact]
    public void Unmounted_csharp_roots_and_node_executables_are_not_silently_ignored()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.File("src/Extra.cs"), "using Root = System.CommandLine.RootCommand; class Extra { object Build() => new System.CommandLine.RootCommand(); Root Implicit() => new(); Root Field = new(); }");
        File.WriteAllText(temp.File("package.json"), """{"bin":{"extra":"cli.js"},"scripts":{"run":"node cli.js"}}""");
        File.WriteAllText(temp.File("tools/package.json"), """{"bin":"extra.js"}""");
        File.WriteAllText(temp.File("run.cmd"), "@echo hello");
        var rows = Discovery.Sources(temp.Path, ["src/Extra.cs", "package.json", "tools/package.json", "run.cmd", "tests/ignored.cs"]).ToArray();
        Assert.Contains(rows, r => r.Id == "command-root:src/Extra.cs:Build");
        Assert.Contains(rows, r => r.Id == "command-root:src/Extra.cs:Implicit");
        Assert.Contains(rows, r => r.Id == "command-root:src/Extra.cs:top-level");
        Assert.Contains(rows, r => r.Id == "node:package.json:bin:extra=cli.js");
        Assert.Contains(rows, r => r.Id == "node:package.json:scripts:run=node cli.js");
        Assert.Contains(rows, r => r.Id == "node:tools/package.json:bin=extra.js");
        Assert.Contains(rows, r => r.Id.StartsWith("launcher:run.cmd:", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsupported_declarations_and_failed_runtime_exports_fail_closed()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.File("package.json"), """{"bin":["ambiguous"]}""");
        Assert.Throws<InvalidOperationException>(() => Discovery.Sources(temp.Path, ["package.json"]).ToArray());
        Assert.Throws<InvalidOperationException>(() => Discovery.Capture(new FakeProc(), temp.Path, "unsupported"));
        var proc = new FakeProc().Answer(new Ran(0, "[]", ""), "export");
        Assert.Equal("[]", Discovery.Capture(proc, temp.Path, "export"));
        Assert.Empty(Discovery.Decode<string[]>("[]"));
        Assert.Throws<InvalidOperationException>(() => Discovery.Decode<string[]>("null"));
    }

    [Fact]
    public void Runtime_aliases_and_disjoint_partial_ports_pass_but_a_stale_fallback_fails()
    {
        var python = new Discovery.PythonTree(
            [new("py:trace", "desktop-manager chrome-tabs traces", "old", ["macos", "windows"], "chrome-tabs traces")], []);
        var command = new Discovery.CsCommand("chrome-tabs traces", true, ["reports"], false);
        var tree = new Discovery.CsTree([command], [], [], [], []);
        var rows = Discovery.Commands(python, new Dictionary<string, Discovery.CsTree> { ["macos"] = tree, ["windows"] = tree }).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Empty(OwnershipRules.Check(rows.Select(r => r.Id).ToArray(), new OwnershipLedger(rows, [], [], [])));

        var broken = tree with
        {
            Forwarding = [new("chrome-tabs", ["desktop-manager", "chrome-tabs"])],
            Dispatch = [new("chrome-tabs traces", true)],
        };
        Assert.Throws<InvalidOperationException>(() => Discovery.Commands(python, new Dictionary<string, Discovery.CsTree> { ["macos"] = broken }).ToArray());
    }

    [Fact]
    public void Every_real_fallback_is_expanded_and_unknown_or_empty_routes_are_refused()
    {
        var python = new Discovery.PythonTree([new("py:one", "triage verify", "py:verify", ["linux"], null)], []);
        var tree = new Discovery.CsTree([], [new("triage", ["python", "-m", "rig.triage.cli"])], [], [new("triage verify", true)], ["job=rig triage verify"]);
        var rows = Discovery.Commands(python, new Dictionary<string, Discovery.CsTree> { ["linux"] = tree }).ToArray();
        Assert.Contains(rows, r => r.Id == "fallback:linux:triage verify=py:verify");
        Assert.Contains(rows, r => r.Id == "cs-job:linux:job=rig triage verify");
        Assert.Throws<InvalidOperationException>(() => Discovery.Commands(python, new Dictionary<string, Discovery.CsTree>
        {
            ["linux"] = tree with { Forwarding = [new("triage", ["unknown"])] },
        }).ToArray());
        Assert.Throws<InvalidOperationException>(() => Discovery.Commands(python, new Dictionary<string, Discovery.CsTree>
        {
            ["linux"] = tree with { Forwarding = [new("c4", ["python", "-m", "rig.c4_okf.cli"])] },
        }).ToArray());
        Assert.Equal("align", Discovery.Canonical("desktop-manager workspace align"));
        Assert.Equal("desktop workspace name-desktops", Discovery.Canonical("desktop-manager workspace ensure-desktops"));
        Assert.Equal("desktop workspace name-desktops", Discovery.Canonical("desktop-manager workspace sync-desktops"));
        Assert.Equal("desktop help", Discovery.Canonical("desktop-manager help"));
        Assert.Equal("triage verify", Discovery.Canonical("triage verify"));
    }

    [Fact]
    public void Two_runtime_csharp_commands_with_the_same_path_are_refused()
    {
        var command = new Discovery.CsCommand("new", true, [], false);
        var tree = new Discovery.CsTree([command, command], [], [], [], []);
        Assert.Throws<InvalidOperationException>(() => Discovery.Commands(new Discovery.PythonTree([], []), new Dictionary<string, Discovery.CsTree> { ["linux"] = tree }).ToArray());
    }

    [Fact]
    public void Full_discovery_evaluates_project_outputs_and_refuses_unknown_or_repeated_surfaces()
    {
        using var temp = new TempDir();
        const string project = "src/Rig.Cli/Rig.Cli.csproj";
        File.WriteAllText(temp.File(project), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var tree = new Discovery.CsTree([new("one", true, [], false)], [], [], [], [])
        {
            RawAliases = [new("--one", "one")],
            AgentTargets = ["external=outside"],
        };
        var proc = new FakeProc()
            .Answer(new Ran(0, JsonSerializer.Serialize(new Discovery.PythonTree([], [])), ""), "uv")
            .Answer(new Ran(0, JsonSerializer.Serialize(tree), ""), "dotnet", "rig.dll")
            .Answer(new Ran(0, project + '\0', ""), "git", "ls-files")
            .Answer(new Ran(0, "100755 abc 0\tscripts/executable\0", ""), "git", "ls-files", "--stage")
            .Answer(new Ran(0, "Exe\n", ""), "dotnet", "msbuild");
        var rows = Discovery.Read(temp.Path, "rig.dll", proc);
        Assert.Equal(6, rows.Length);
        Assert.Contains(rows, row => row.Id == $"executable-project:{project}:Exe");
        Assert.True(File.Exists(Path.Join(temp.Path, "temp", "command-ownership-probes.json")));
        Assert.Contains(proc.Requests, request => request.Argv.Contains("macos", StringComparer.Ordinal));
        proc.Answer(new Ran(0, "Library", ""), "dotnet", "msbuild");
        Assert.Equal(5, Discovery.Read(temp.Path, "rig.dll", proc).Length);
        proc.Answer(new Ran(0, "Unsupported", ""), "dotnet", "msbuild");
        Assert.Throws<InvalidOperationException>(() => Discovery.Read(temp.Path, "rig.dll", proc));
        proc.Answer(new Ran(0, "Exe", ""), "dotnet", "msbuild")
            .Answer(new Ran(0, JsonSerializer.Serialize(new Discovery.PythonTree([], ["same", "same"])), ""), "uv");
        Assert.Throws<InvalidOperationException>(() => Discovery.Read(temp.Path, "rig.dll", proc));
        const string unknown = "src/New/New.csproj";
        File.WriteAllText(temp.File(unknown), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        proc.Answer(new Ran(0, unknown + '\0', ""), "git", "ls-files");
        Assert.Contains("Unsupported executable project", Assert.Throws<InvalidOperationException>(() => Discovery.Read(temp.Path, "rig.dll", proc)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Named_console_shims_are_aliases_and_cannot_claim_nonexistent_csharp_commands()
    {
        var alias = Discovery.EntrySurface("script:missing=rig.csharp_cli:entry");
        Assert.Equal("cs:missing", alias.AliasTo);
        Assert.Contains(OwnershipRules.Check([alias.Id], new OwnershipLedger([alias], [], [], [])), e => e.StartsWith("Alias must dispatch", StringComparison.Ordinal));
        Assert.Null(Discovery.EntrySurface("script:rig=rig.csharp_cli:entry").AliasTo);
        Assert.Null(Discovery.EntrySurface("script:desktop-manager=desktop_manager.cli:main").AliasTo);
    }

    [Fact]
    public void A_migrated_align_cannot_coexist_with_its_legacy_desktop_spelling()
    {
        var python = new Discovery.PythonTree([new("py:align", "desktop-manager workspace align", "py:align", ["windows"], null)], []);
        var tree = new Discovery.CsTree([new("align", true, [], false)], [], [], [], []);
        var rows = Discovery.Commands(python, new Dictionary<string, Discovery.CsTree> { ["windows"] = tree }).ToArray();
        Assert.Contains(OwnershipRules.Check(rows.Select(row => row.Id).ToArray(), new OwnershipLedger(rows, [], [], [])), error => error.StartsWith("Overlapping implementations: align|windows", StringComparison.Ordinal));
    }
}
