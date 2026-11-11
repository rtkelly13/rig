using System.CommandLine;
using Rig.Cli.Skills;
using Rig.Core.Processes;
using Rig.Core.Skills;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Skills;

namespace Rig.Cli.Tests.Skills;

/// <summary>
/// <c>rig skills</c>'s output and exit codes; ported from the Python <c>rig.skills.cli</c>. The checkout, home and
/// clones are all temp folders, and git is a fake.
/// </summary>
public sealed class SkillsCommandTests : IDisposable
{
    private readonly TempDir _dir = new TempDir().AsCodeRoot();
    private readonly FakeProc _proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "git");
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly string _checkout;
    private readonly string _home;

    public SkillsCommandTests()
    {
        _checkout = _dir.Folder("personal/rig");
        _home = _dir.Folder("home");
        var skills = _dir.Folder("personal/rig/skills");
        SkillFixtures.Write(skills, "engineering/tdd", "tdd", "Test-driven development");
        SkillFixtures.Write(skills, "productivity/disk-cleanup", "disk-cleanup", new string('d', 70));
        SkillFixtures.Write(skills, "hidden", "hidden", "Not curated");
        File.WriteAllText(_dir.File("personal/rig/config/skills-allowlist.json"), "[\"tdd\", \"disk-cleanup\"]");
    }

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _dir.Dispose();
    }

    private string Output => _out.ToString().ReplaceLineEndings("\n");

    private string Claude => SkillSystems.Directory("claude", _home);

    private int Run(params string[] args) =>
        new SkillsCommand(new SkillsHost(_checkout, _home, _checkout, Path.Join(_dir.Path, "external", "skills"), false), new Git(_proc, _dir.Path), new SymlinkLinker(), _out, _err)
            .Build().Parse(args).Invoke(new InvocationConfiguration { Output = _out, Error = _err });

    private Task<int> RunAsync(params string[] args) =>
        new SkillsCommand(new SkillsHost(_checkout, _home, _checkout, Path.Join(_dir.Path, "external", "skills"), false), new Git(_proc, _dir.Path), new SymlinkLinker(), _out, _err)
            .Build().Parse(args).InvokeAsync(new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);

    private void Registry(string json) => File.WriteAllText(_dir.File("personal/rig/config/upstream-skills.json"), json);

    [Fact]
    public void Status_reports_missing_folders_then_health_after_sync()
    {
        Assert.Equal(0, Run("status"));
        Assert.StartsWith("AI Agent Skills Symlink Verification (2 active skills, allowlist active)\n\n", Output, StringComparison.Ordinal);
        Assert.Contains("Missing Dir", Output, StringComparison.Ordinal);
        Assert.EndsWith("Run 'rig skills sync' to provision or fix missing symlinks.\n", Output, StringComparison.Ordinal);

        Assert.Equal(0, Run("sync"));
        Assert.Contains("Reconciling 2 selected skills across 4 CLI system(s):\n  • OpenCode\n", Output, StringComparison.Ordinal);
        Assert.Contains("✓ Claude Code: Successfully linked 2 skill(s)", Output, StringComparison.Ordinal);
        Assert.EndsWith("✓ Symlink synchronization complete across all requested systems!\n", Output, StringComparison.Ordinal);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("status", "-s", "claude"));
        Assert.Contains("✓ Healthy", Output, StringComparison.Ordinal);
        Assert.Contains("2/2", Output, StringComparison.Ordinal);
        Assert.EndsWith("✓ All AI agent systems are completely in sync!\n", Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_lists_each_kind_of_drift()
    {
        Assert.Equal(0, Run("sync", "-s", "claude"));
        DirectoryLinks.RemoveLink(Path.Join(Claude, "tdd"));
        Directory.CreateSymbolicLink(Path.Join(Claude, "tdd"), _dir.Folder("elsewhere"));
        Directory.CreateSymbolicLink(Path.Join(Claude, "gone"), Path.Join(_dir.Path, "gone"));
        Directory.CreateSymbolicLink(Path.Join(Claude, "stale"), _dir.Folder("stale"));
        DirectoryLinks.RemoveLink(Path.Join(Claude, "disk-cleanup"));
        _out.GetStringBuilder().Clear();

        Assert.Equal(0, Run("status", "-s", "claude"));

        Assert.Contains("! Drifted", Output, StringComparison.Ordinal);
        Assert.Contains("1 missing, 1 broken, 1 stale, 1 drifted", Output, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Join(Claude, "disk-cleanup"));
        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("status", "-s", "claude"));
        Assert.Contains("1 non-symlink collision", Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dry_run_sync_changes_nothing()
    {
        Assert.Equal(0, Run("sync", "--dry-run", "-s", "all"));

        Assert.StartsWith("Running in dry-run mode (no changes will be applied)\n\nReconciling 2 selected skills across 5 CLI system(s):", Output, StringComparison.Ordinal);
        Assert.Contains("✓ Cross-Agent Standard: Would link 2 skill(s)", Output, StringComparison.Ordinal);
        Assert.DoesNotContain("synchronization complete", Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Claude));
    }

    [Fact]
    public void Sync_with_only_or_all_activates_an_overlay()
    {
        Assert.Equal(2, Run("sync", "--all", "--only", "hidden"));
        Assert.Equal("Use either --all or --only, not both.\n", Output);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("sync", "--only", "hidden", "--dry-run", "-s", "claude"));
        Assert.Equal("Activating hidden as a reversible skill overlay:\n  Claude Code: 1 link(s)\n", Output);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("sync", "--all", "-s", "claude"));
        Assert.Equal("Activating all discovered as a reversible skill overlay:\n  Claude Code: 3 link(s)\nRun 'rig skills deactivate --all' to restore the curated catalog.\n", Output);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("status", "-s", "claude"));
        Assert.Contains("\nTemporary overlay: 1 skill(s) active; run 'rig skills deactivate --all' to restore the curated catalog.\n", Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Activate_and_deactivate_take_exactly_one_selection()
    {
        Assert.Equal(2, Run("activate"));
        Assert.Equal(2, Run("deactivate", "--all", "--only", "x"));
        Assert.Equal("Choose exactly one of --only or --all.\nChoose exactly one of --only or --all.\n", Output);
    }

    [Fact]
    public void Activate_then_deactivate_restores_the_catalog()
    {
        Assert.Equal(2, Run("activate", "--only", "nope"));
        Assert.Equal("Unknown skill(s): nope\n", Output);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("activate", "--only", "hidden", "-s", "claude", "-s", "codex"));
        Assert.Equal("Claude Code: 1 link(s)\nOpenAI Codex: 1 link(s)\n", Output);
        Assert.True(DirectoryLinks.IsLink(Path.Join(Claude, "hidden")));

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("deactivate", "--all", "-s", "claude", "-s", "codex"));
        Assert.Equal("Claude Code: removed 1 link(s)\nOpenAI Codex: removed 1 link(s)\n", Output);
        Assert.False(DirectoryLinks.Occupied(Path.Join(Claude, "hidden")));
    }

    [Fact]
    public void Prune_reports_nothing_or_each_system()
    {
        Assert.Equal(0, Run("prune"));
        Assert.Equal("✓ No broken symlinks found across target systems.\n", Output);

        Directory.CreateDirectory(Claude);
        Directory.CreateSymbolicLink(Path.Join(Claude, "gone"), Path.Join(_dir.Path, "gone"));
        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("prune", "--dry-run", "-s", "claude"));
        Assert.Equal("  • Claude Code: Would prune 1 broken symlink(s)\n\n✓ Done (1 broken symlink(s) handled).\n", Output);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("prune", "-s", "claude"));
        Assert.StartsWith("  • Claude Code: Pruned 1 broken symlink(s)", Output, StringComparison.Ordinal);
        Assert.False(DirectoryLinks.Occupied(Path.Join(Claude, "gone")));
    }

    [Fact]
    public void List_tabulates_curated_skills_and_filters_by_query()
    {
        Assert.Equal(0, Run("list"));
        Assert.StartsWith("Skills Catalog (2 total)", Output, StringComparison.Ordinal);
        Assert.Contains(new string('d', 60) + "...", Output, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", Output, StringComparison.Ordinal);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, Run("list", "-q", "TEST-DRIVEN"));
        Assert.StartsWith("Skills Catalog (1 total)", Output, StringComparison.Ordinal);
        Assert.Contains("shared-utilities", Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("sync")]
    [InlineData("update")]
    public void An_unknown_system_is_rejected(string command)
    {
        Assert.Equal(1, Run(command, "-s", "unknown-tool"));
        Assert.Equal("Error: Unknown system 'unknown-tool'. Supported systems: opencode, claude, codex, antigravity, agents\n", Output);
    }

    [Fact]
    public void A_filesystem_failure_is_reported_not_thrown()
    {
        File.WriteAllText(_dir.File("home/.claude/skills"), "not a folder");

        Assert.Equal(1, Run("sync", "-s", "claude"));
        Assert.StartsWith("rig skills: ", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Home_can_be_overridden()
    {
        var other = _dir.Folder("other-home");

        Assert.Equal(0, Run("sync", "-s", "claude", "--home", other));

        Assert.True(DirectoryLinks.IsLink(Path.Join(SkillSystems.Directory("claude", other), "tdd")));
        Assert.False(Directory.Exists(Claude));
    }

    [Fact]
    public async Task Update_vendors_then_syncs()
    {
        Registry("""[{"name": "up", "url": "https://example.com/up.git"}]""");
        _dir.Folder("external/skills/up/.git");
        SkillFixtures.Write(Path.Join(_dir.Path, "external", "skills", "up"), "skills/tdd-extra", "tdd-extra");

        Assert.Equal(0, await RunAsync("update", "-s", "claude"));

        Assert.StartsWith("Fetching & Updating Upstream Vendored Skills\nUpdating up from https://example.com/up.git...\n✓ Updated up\n✓ Extracted 1 skills from up\n\n✓ Successfully vendored 1 skills across 1 repos.\n\n✓ Claude Code: Synced 2 skill(s)\n", Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(_checkout, "skills", "vendored", "up", "tdd-extra", "SKILL.md")));
        Assert.True(DirectoryLinks.IsLink(Path.Join(Claude, "tdd")));
    }

    [Fact]
    public async Task Update_without_sync_leaves_links_alone()
    {
        Registry("""[{"name": "up", "url": "u"}]""");
        _dir.Folder("external/skills/up/.git");

        Assert.Equal(0, await RunAsync("update", "--vendor", "up", "--no-sync"));

        Assert.EndsWith("✓ Successfully vendored 0 skills across 1 repos.\n\n", Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Claude));
    }

    [Fact]
    public async Task Update_rejects_an_unknown_vendor()
    {
        Assert.Equal(1, await RunAsync("update", "-v", "nobody"));

        Assert.EndsWith("Error: Vendor 'nobody' not found in configuration.\n", Output, StringComparison.Ordinal);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Update_reports_a_failed_clone_or_bad_registry()
    {
        _proc.Answer(new Ran(128, string.Empty, "denied"), "git");
        Assert.Equal(1, await RunAsync("update", "-v", "pstack"));
        Assert.Equal("rig skills: Git clone failed for pstack: denied\n", _err.ToString().ReplaceLineEndings("\n"));

        Registry("{}");
        Assert.Equal(1, await RunAsync("update"));
        Assert.Contains("Schema validation error", _err.ToString(), StringComparison.Ordinal);
    }
}
