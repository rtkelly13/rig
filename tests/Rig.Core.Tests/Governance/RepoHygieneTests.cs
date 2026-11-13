using System.Text.Json.Nodes;
using Rig.Core.Governance;

namespace Rig.Core.Tests.Governance;

/// <summary><see cref="RepoHygiene"/> over temp repos. Ported from the repo hygiene tests.</summary>
public sealed class RepoHygieneTests : IDisposable
{
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private string Repo => _root.Folder("app");

    private static RepoPolicies Policy(string json) => RepoPolicies.Parse(JsonNode.Parse(json)!.AsObject(), "policy.json");

    private void Put(string relative, string content) => File.WriteAllText(_root.File($"app/{relative}"), content);

    private string Read(string relative) => File.ReadAllText(Path.Join(Repo, relative));

    [Fact]
    public void Agents_md_is_created_then_reads_as_configured()
    {
        Assert.Equal("MISSING", RepoHygiene.InspectAgentsMd(Repo).Status);
        Assert.Equal("created", RepoHygiene.AlignAgentsMd(Repo, dryRun: true).Status);
        Assert.False(File.Exists(Path.Join(Repo, "AGENTS.md")));

        Assert.Equal("created", RepoHygiene.AlignAgentsMd(Repo, dryRun: false).Status);

        Assert.StartsWith("# APP\n\n## 🛑", Read("AGENTS.md"), StringComparison.Ordinal);
        Assert.Equal(new HygieneResult("CONFIGURED", true), RepoHygiene.InspectAgentsMd(Repo));
        Assert.Equal("already_configured", RepoHygiene.AlignAgentsMd(Repo, dryRun: false).Status);
    }

    [Fact]
    public void Agents_md_without_the_section_gets_it_appended_and_a_divergent_one_is_left()
    {
        Put("AGENTS.md", "# App\n");
        Assert.Equal("PARTIAL", RepoHygiene.InspectAgentsMd(Repo).Status);

        Assert.Equal("appended", RepoHygiene.AlignAgentsMd(Repo, dryRun: false).Status);
        Assert.StartsWith("# App\n\n\n## 🛑", Read("AGENTS.md"), StringComparison.Ordinal);

        Put("AGENTS.md", "# App\n\n## 🛑 Repository Conventions & Workflow Policy\n\n1. Something else.\n");
        var divergent = RepoHygiene.AlignAgentsMd(Repo, dryRun: false);
        Assert.Equal("divergent", divergent.Status);
        Assert.Contains("agents-audit", divergent.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Npmrc_is_only_for_node_repos_and_gets_the_registry_prepended()
    {
        Assert.Equal(new HygieneResult("N/A (No package.json)", true), RepoHygiene.InspectNpmrc(Repo));
        Assert.Equal("skipped", RepoHygiene.AlignNpmrc(Repo, dryRun: false).Status);

        Put("package.json", "{}");
        Put(".npmrc", "save-exact=true\n");
        Assert.Equal("MISSING / DRIFTED", RepoHygiene.InspectNpmrc(Repo).Status);
        Assert.Equal("aligned", RepoHygiene.AlignNpmrc(Repo, dryRun: false).Status);

        Assert.Equal($"{RepoHygiene.NpmrcRegistry}\nsave-exact=true\n", Read(".npmrc"));
        Assert.True(RepoHygiene.InspectNpmrc(Repo).IsAligned);
        Assert.Equal("already_configured", RepoHygiene.AlignNpmrc(Repo, dryRun: false).Status);
    }

    [Fact]
    public void Unpinned_action_refs_are_counted()
    {
        Assert.Equal(new HygieneResult("N/A (No Workflows)", true), RepoHygiene.InspectPinnedShas(Repo));

        Put(".github/workflows/ci.yml", $"""
            jobs:
              ci:
                steps:
                  - uses: actions/checkout@v4
                  - uses: actions/setup-node@{new string('a', 40)} # v4
                  - uses: ./local-action
            """);
        Put(".github/workflows/release.yaml", "      - uses: actions/cache@main\n");

        Assert.Equal(new HygieneResult("UNPINNED (2 actions)", UnpinnedCount: 2), RepoHygiene.InspectPinnedShas(Repo));
    }

    [Fact]
    public void Fully_pinned_workflows_read_as_pinned()
    {
        Put(".github/workflows/ci.yml", $"      - uses: actions/checkout@{new string('0', 40)}\n");

        Assert.Equal(new HygieneResult("PINNED", true), RepoHygiene.InspectPinnedShas(Repo));
    }

    [Fact]
    public void Quoted_and_reusable_workflow_refs_are_checked_too()
    {
        Put(".github/workflows/ci.yml", """
                  - uses: "actions/checkout@v4"
                  - uses: 'actions/cache@main'
              build:
                uses: acme/workflows/.github/workflows/build.yml@main
            """);

        Assert.Equal(new HygieneResult("UNPINNED (3 actions)", UnpinnedCount: 3), RepoHygiene.InspectPinnedShas(Repo));
    }

    [Fact]
    public void Markers_only_in_comments_are_not_a_guard()
    {
        Put(".git/hooks/pre-push", "#!/bin/sh\n# DIRECT PUSH TO MAIN BLOCKED\n# git lfs pre-push\nexit 0\n");

        Assert.Equal("MISSING", RepoHygiene.InspectGitHooks(Repo).Status);
        Assert.Equal("aligned", RepoHygiene.AlignGitHooks(Repo, dryRun: true).Status);
    }

    [Fact]
    public void Hooks_need_a_hooks_folder()
    {
        Assert.Equal("MISSING", RepoHygiene.InspectGitHooks(Repo).Status);
        Assert.Equal(new HygieneResult("skipped", Reason: "No .git/hooks directory found"), RepoHygiene.AlignGitHooks(Repo, dryRun: false));
    }

    [Fact]
    public void A_hook_without_the_lfs_handoff_is_outdated()
    {
        Put(".git/hooks/pre-push", "echo DIRECT PUSH TO MAIN BLOCKED\n");

        Assert.Equal("OUTDATED", RepoHygiene.InspectGitHooks(Repo).Status);
        Assert.Equal("aligned", RepoHygiene.AlignGitHooks(Repo, dryRun: true).Status);
        Assert.Equal("echo DIRECT PUSH TO MAIN BLOCKED\n", Read(".git/hooks/pre-push"));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void The_installed_hook_is_executable_and_current()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "file modes are POSIX-only");
        _ = _root.Folder("app/.git/hooks");

        Assert.Equal("aligned", RepoHygiene.AlignGitHooks(Repo, dryRun: false).Status);

        var hook = Path.Join(Repo, ".git", "hooks", "pre-push");
        Assert.Equal(RepoHygiene.PrePushHookContent, File.ReadAllText(hook));
        Assert.True(File.GetUnixFileMode(hook).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(new HygieneResult("ENFORCED", true), RepoHygiene.InspectGitHooks(Repo));
        Assert.Equal("already_configured", RepoHygiene.AlignGitHooks(Repo, dryRun: false).Status);
    }

    [Fact]
    public void A_lowercase_todo_is_renamed_keeping_its_content()
    {
        Put("todo.md", "- [ ] mine\n");

        Assert.Equal("MISSING", RepoHygiene.InspectTodoMd(Repo).Status);
        Assert.Equal("renamed_to_uppercase", RepoHygiene.AlignTodoMd(Repo, dryRun: true).Status);
        Assert.Equal("MISSING", RepoHygiene.InspectTodoMd(Repo).Status);
        Assert.Equal("renamed_to_uppercase", RepoHygiene.AlignTodoMd(Repo, dryRun: false).Status);

        Assert.Equal(["TODO.md"], Directory.EnumerateFileSystemEntries(Repo).Select(Path.GetFileName));
        Assert.Equal("- [ ] mine\n", Read("TODO.md"));
        Assert.Equal(new HygieneResult("PRESENT", true), RepoHygiene.InspectTodoMd(Repo));
        Assert.Equal("already_exists", RepoHygiene.AlignTodoMd(Repo, dryRun: false).Status);
    }

    [Fact]
    public void A_missing_todo_is_created()
    {
        Assert.Equal("aligned", RepoHygiene.AlignTodoMd(Repo, dryRun: false).Status);

        Assert.Equal("# TODO: APP\n\n- [ ] Local tasks and notes (gitignored)\n", Read("TODO.md"));
    }

    [Fact]
    public void Ls_lint_without_a_policy_needs_only_the_shell_bans()
    {
        Assert.Equal("MISSING", RepoHygiene.InspectLsLint(Repo, null, "app").Status);
        Put(".ls-lint.yml", "ls:\n  .sh: exists:0\n");
        Assert.Equal("INCOMPLETE_RULES", RepoHygiene.InspectLsLint(Repo, null, "app").Status);

        Assert.Equal("aligned", RepoHygiene.AlignLsLint(Repo, null, "app", dryRun: false).Status);

        Assert.Equal(RepoHygiene.RenderLsLintConfig([], [], []), Read(".ls-lint.yml"));
        Assert.DoesNotContain("scripts:", Read(".ls-lint.yml"), StringComparison.Ordinal);
        Assert.True(RepoHygiene.InspectLsLint(Repo, null, "app").IsAligned);
        Assert.Equal("already_configured", RepoHygiene.AlignLsLint(Repo, null, "app", dryRun: false).Status);
    }

    [Fact]
    public void Ls_lint_carries_the_policy_exceptions_and_script_rules()
    {
        var policy = Policy("""
            {"defaults": {"script_ban": {"allowed_exceptions": ["tools/legacy.sh"], "allowed_script_extensions": ["cs"], "banned_script_extensions": [".py"]}}}
            """);
        Put(".ls-lint.yml", RepoHygiene.RenderLsLintConfig([], [], []));
        Assert.Equal(
            new HygieneResult("MISSING_EXCEPTIONS", File: Path.Join(Repo, ".ls-lint.yml"), Reason: "Missing allowed exception: tools/legacy.sh"),
            RepoHygiene.InspectLsLint(Repo, policy, "app"));
        Put(".ls-lint.yml", RepoHygiene.RenderLsLintConfig(["tools/legacy.sh"], [], []));
        Assert.Equal("MISSING_SCRIPTS_RULES", RepoHygiene.InspectLsLint(Repo, policy, "app").Status);
        Put(".ls-lint.yml", RepoHygiene.RenderLsLintConfig(["tools/legacy.sh"], [".cs"], []));
        Assert.Equal("Missing banned script extension: .py", RepoHygiene.InspectLsLint(Repo, policy, "app").Reason);
        Put(".ls-lint.yml", RepoHygiene.RenderLsLintConfig(["tools/legacy.sh"], [], [".py"]));
        Assert.Equal("Missing allowed script extension: .cs", RepoHygiene.InspectLsLint(Repo, policy, "app").Reason);

        Assert.Equal("aligned", RepoHygiene.AlignLsLint(Repo, policy, "app", dryRun: false).Status);

        var config = Read(".ls-lint.yml");
        Assert.Contains("  scripts:\n    .dir: regex:.*\n", config, StringComparison.Ordinal);
        Assert.Contains("    .cs: regex:.*\n    .py: exists:0\n    .*: exists:0\n  \"scripts/**\":\n", config, StringComparison.Ordinal);
        Assert.EndsWith("  # Explicit repository exceptions:\n  - tools/legacy.sh\n", config, StringComparison.Ordinal);
        Assert.True(RepoHygiene.InspectLsLint(Repo, policy, "app").IsAligned);
    }

    [Fact]
    public void A_disabled_ban_checks_no_exceptions_and_a_narrower_ban_checks_only_its_extensions()
    {
        var disabled = Policy("""{"defaults": {"script_ban": {"enabled": false, "allowed_exceptions": ["x.sh"]}}}""");
        var narrow = Policy("""{"defaults": {"script_ban": {"banned_extensions": [".ps1"]}}}""");
        Put(".ls-lint.yml", "ls:\n  .sh: exists:0\n  .ps1: exists:0\n");

        Assert.True(RepoHygiene.InspectLsLint(Repo, disabled, "app").IsAligned);
        Put(".ls-lint.yml", "ls:\n  .ps1: exists:0\n");
        Assert.True(RepoHygiene.InspectLsLint(Repo, narrow, "app").IsAligned);
    }

    [Fact]
    public void Align_renders_only_the_extensions_the_ban_policy_names()
    {
        var narrow = Policy("""{"defaults": {"script_ban": {"banned_extensions": ["ps1"]}}}""");
        var disabled = Policy("""{"defaults": {"script_ban": {"enabled": false}}}""");

        RepoHygiene.AlignLsLint(Repo, narrow, "app", dryRun: false);

        Assert.StartsWith("# .ls-lint.yml\n# Ban on .ps1 scripts, as this repo's script_ban policy sets.\n", Read(".ls-lint.yml"), StringComparison.Ordinal);
        Assert.Contains("ls:\n  .ps1: exists:0\n  \"**\":\n    .ps1: exists:0\n", Read(".ls-lint.yml"), StringComparison.Ordinal);
        Assert.DoesNotContain(".sh:", Read(".ls-lint.yml"), StringComparison.Ordinal);
        Assert.True(RepoHygiene.InspectLsLint(Repo, narrow, "app").IsAligned);
        Put(".ls-lint.yml", "ls:\n  .sh: exists:0\n");
        Assert.Equal("INCOMPLETE_RULES", RepoHygiene.InspectLsLint(Repo, narrow, "app").Status);

        File.Delete(Path.Join(Repo, ".ls-lint.yml"));
        RepoHygiene.AlignLsLint(Repo, disabled, "app", dryRun: false);

        Assert.Contains("# This repo's script_ban policy bans no scripts.\n", Read(".ls-lint.yml"), StringComparison.Ordinal);
        Assert.Contains("\nls: {}\n", Read(".ls-lint.yml"), StringComparison.Ordinal);
        Assert.DoesNotContain("exists:0", Read(".ls-lint.yml"), StringComparison.Ordinal);
        Assert.True(RepoHygiene.InspectLsLint(Repo, disabled, "app").IsAligned);
    }

    [Fact]
    public void Renovate_alone_or_with_security_only_dependabot_is_fine()
    {
        Assert.Equal(new HygieneResult("NO_RENOVATE", true), RepoHygiene.InspectDependabotRenovate(Repo));
        Put("renovate.json", "{}");
        Assert.Equal(new HygieneResult("CONFIGURED", true, Path.Join(Repo, "renovate.json")), RepoHygiene.InspectDependabotRenovate(Repo));

        Put(".github/dependabot.yml", "version: 2\nupdates:\n  - package-ecosystem: npm\n    open-pull-requests-limit: 0\n");

        Assert.True(RepoHygiene.InspectDependabotRenovate(Repo).IsAligned);
    }

    [Fact]
    public void Renovate_beside_dependabot_version_updates_conflicts()
    {
        Put("renovate.json", "{}");
        Put(".github/dependabot.yaml", "version: 2\nupdates:\n  - package-ecosystem: npm\n  - package-ecosystem: nuget\n    open-pull-requests-limit: 5\n");

        var result = RepoHygiene.InspectDependabotRenovate(Repo);

        Assert.Equal("CONFLICT", result.Status);
        Assert.StartsWith("Conflicting dependency automation: found both Renovate config (renovate.json) and active version updates in .github/dependabot.yaml.", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("version: 2\n", false)]
    [InlineData("version: 2\nupdates:\n", true)]
    [InlineData("version: 2\nupdates: []\n", false)]
    [InlineData("updates: not-a-list\n", true)]
    [InlineData("updates:\n  - just-a-string\n", true)]
    [InlineData("updates:\n  - open-pull-requests-limit: lots\n", true)]
    [InlineData("updates: [unclosed\n", true)]
    [InlineData("- a list\n", false)]
    public void Dependabot_updates_are_read_from_its_shape_or_else_from_the_text(string config, bool expected)
    {
        Put(".github/dependabot.yml", config);

        Assert.Equal(expected, RepoHygiene.HasDependabotVersionUpdates(Repo));
    }

    [Fact]
    public void Editorconfig_missing_or_invalid_is_reported()
    {
        Assert.Equal("MISSING", RepoHygiene.InspectEditorconfig(Repo).Status);

        Put(".editorconfig", "[*]\nindent_size = 4\n");
        var invalid = RepoHygiene.InspectEditorconfig(Repo);
        Assert.Equal("INVALID", invalid.Status);
        Assert.Equal("Missing 'root = true'", invalid.Reason);

        Put(".editorconfig", "root = true\n\n[*]\nindent_size = 4\n");
        Assert.Equal("CONFIGURED", RepoHygiene.InspectEditorconfig(Repo).Status);
    }

    [Fact]
    public void Editorconfig_in_dotnet_repo_checks_for_csharp_rules()
    {
        Put("global.json", "{}\n");
        Put(".editorconfig", "root = true\n\n[*]\nindent_size = 4\n");

        var partial = RepoHygiene.InspectEditorconfig(Repo);
        Assert.Equal("PARTIAL", partial.Status);
        Assert.Equal(".NET repository missing [*.cs] rules", partial.Reason);

        Put(".editorconfig", "root = true\n\n[*.cs]\nindent_size = 4\n");
        Assert.Equal("CONFIGURED", RepoHygiene.InspectEditorconfig(Repo).Status);
    }

    [Fact]
    public void Missing_editorconfig_is_created_from_canonical_template()
    {
        Assert.Equal("MISSING", RepoHygiene.InspectEditorconfig(Repo).Status);

        var dry = RepoHygiene.AlignEditorconfig(Repo, dryRun: true);
        Assert.Equal("aligned", dry.Status);
        Assert.Equal("MISSING", RepoHygiene.InspectEditorconfig(Repo).Status);

        var run = RepoHygiene.AlignEditorconfig(Repo, dryRun: false);
        Assert.Equal("aligned", run.Status);
        Assert.Equal("CONFIGURED", RepoHygiene.InspectEditorconfig(Repo).Status);
        Assert.Contains("root = true", Read(".editorconfig"), StringComparison.Ordinal);
        Assert.Contains("[*.cs]", Read(".editorconfig"), StringComparison.Ordinal);

        var already = RepoHygiene.AlignEditorconfig(Repo, dryRun: false);
        Assert.Equal("already_configured", already.Status);
    }
}
