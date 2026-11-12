using Rig.Core.Policy;

namespace Rig.Core.Tests.Policy;

/// <summary>Ported from the audit cases of the Python <c>tests/test_policy.py</c>.</summary>
public sealed class ProjectAuditTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private void Write(string relative, string text) => File.WriteAllText(_dir.File(relative), text);

    [Fact]
    public void Reads_python_and_node_manifests()
    {
        Write("package.json", """{"scripts":{"test":"vitest","build":"next build","odd":3}}""");
        Write("pyproject.toml", "[project.scripts]\ncheck = \"repo_health.cli:main\"\n");

        var commands = ProjectAudit.Commands(_dir.Path);

        Assert.Equal(
            [("pnpm build", CommandRisk.Local), ("pnpm test", CommandRisk.Safe), ("uv run check", CommandRisk.Safe)],
            commands.Select(c => (c.Invocation, c.Risk)));
        Assert.All(commands, c => Assert.Equal(Path.GetFileName(_dir.Path), c.Project));
    }

    [Fact]
    public void Child_projects_are_read_but_hidden_folders_are_skipped()
    {
        Write("b/package.json", """{"scripts":{"deploy":"vercel"}}""");
        Write("a/package.json", """{"name":"no scripts"}""");
        Write("a/pyproject.toml", "[project.scripts]\nserve = 'app:serve'\n");
        Write(".hidden/package.json", """{"scripts":{"lint":"eslint"}}""");

        var commands = ProjectAudit.Commands(_dir.Path);

        Assert.Equal(
            [("a", "uv run serve", CommandRisk.Local), ("b", "pnpm deploy", CommandRisk.External)],
            commands.Select(c => (c.Project, c.Invocation, c.Risk)));
    }

    [Theory]
    [InlineData("release", "npm publish", CommandRisk.External)]
    [InlineData("fmt", "prettier --write .", CommandRisk.Local)]
    [InlineData("typecheck", "tsc", CommandRisk.Safe)]
    [InlineData("misc", "node x.js", CommandRisk.Local)]
    public void A_script_is_tiered_by_its_name_and_command(string name, string source, CommandRisk risk) =>
        Assert.Equal(risk, ProjectAudit.Risk(name, source));

    [Fact]
    public void Only_the_project_scripts_table_is_read()
    {
        const string toml = """
            [project]
            name = "x"

            [ project.scripts ]  # entry points
            plain = "a:b"
            "quoted-key" = "c:d"  # note
            'literal' = 'e\f'
            escaped = "g\"h"
            not a script line

            [tool.other]
            ignored = "z"
            """;

        Assert.Equal(
            [("plain", "a:b"), ("quoted-key", "c:d"), ("literal", "e\\f"), ("escaped", "g\"h")],
            ProjectAudit.ProjectScripts(toml));
    }

    [Fact]
    public void History_is_counted_by_family_without_exposing_commands()
    {
        var history = _dir.File(".zsh_history");
        File.WriteAllText(history, ": 1:0;uv run pytest tests/test_x.py --token=secret\n: 2:0;pnpm typecheck\r\n: 3:0;git status\r: 4:0;git log\n: 5:0;workspace status\n: 6:0;ls\n");

        Assert.Equal(
            [("git inspection", 2), ("uv validation", 1), ("pnpm validation", 1), ("workspace inspection", 1)],
            ProjectAudit.History(history));
    }

    [Fact]
    public void The_rig_front_door_is_counted_apart_from_its_uv_scripts()
    {
        var history = _dir.File(".zsh_history");
        File.WriteAllText(history, ": 1:0;rig help\n: 2:0;rig status --tests\n: 3:0;uv run status\n: 4:0;my-rig status\n: 5:0;rig workspace status\n");

        Assert.Equal(
            [("rig validation", 2), ("uv validation", 1), ("workspace inspection", 1)],
            ProjectAudit.History(history));
    }

    [Fact]
    public void No_history_file_means_no_counts() => Assert.Empty(ProjectAudit.History(Path.Join(_dir.Path, "none")));
}
