using System.Text.Json.Nodes;
using CsCheck;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>
/// Properties of <see cref="Gitignores.AlignAsync"/> over random <c>.gitignore</c> files: a dry run writes nothing,
/// alignment only appends, leaves the repo aligned, and a second run changes nothing.
/// </summary>
public sealed class GitignorePropertyTests
{
    private static readonly string[] Lines =
        ["temp/", "/temp", ".temp", "TODO.md", "todo.md", "[Tt][Oo][Dd][Oo].md", "bin/", "**/obj", "venv", "node_modules", "__pycache__/", "# temp/", "!keep", "*.log", ""];

    private static readonly string[] Separators = ["\n", "\r\n", "\r"];

    private static readonly RepoPolicies Policies = RepoPolicies.Parse(
        JsonNode.Parse("""
            {
              "defaults": {
                "gitignore_entries": [
                  {"name": "temp", "comment": "Temp", "write": ["temp/"]},
                  {"name": "todo", "comment": "TODO", "write": ["TODO.md", "todo.md"], "match_mode": "all"},
                  {"name": "venv", "comment": "Envs", "write": [".venv/", "venv/"], "satisfied_by": ["venv"]}
                ],
                "languages": ["python"]
              },
              "language_presets": {"python": {"gitignore_entries": [{"name": "pyc", "comment": "Bytecode", "write": ["__pycache__/"]}]}}
            }
            """),
        "policy.json");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Alignment_is_append_only_complete_and_idempotent()
    {
        var gen = Gen.Select(Gen.Int[0, Lines.Length - 1].Array[0, 8], Gen.Int[0, Separators.Length - 1], Gen.Bool);
        await gen.SampleAsync(async sample => await CheckAsync(sample.Item1, sample.Item2, sample.Item3), iter: 40, threads: 1);
    }

    private static async Task CheckAsync(int[] picks, int separator, bool exists)
    {
        using var root = new TempDir();
        var repo = Path.GetDirectoryName(root.Folder("personal/r/.git"))!;
        var file = Path.Join(repo, ".gitignore");
        var before = string.Join(Separators[separator], picks.Select(i => Lines[i]));
        if (exists)
        {
            await File.WriteAllTextAsync(file, before, Token);
        }

        var subject = new Gitignores(new Git(new FakeProc(), root.Path), new EstateScopes([], []), Policies);

        var dry = await subject.AlignAsync(repo, dryRun: true, cancellationToken: Token);
        Assert.Equal(exists, File.Exists(file));
        Assert.True(!exists || before == await File.ReadAllTextAsync(file, Token));

        var wet = await subject.AlignAsync(repo, cancellationToken: Token);
        Assert.Equal(dry.Added, wet.Added);
        var after = File.Exists(file) ? await File.ReadAllTextAsync(file, Token) : string.Empty;
        Assert.StartsWith(exists ? before : string.Empty, after, StringComparison.Ordinal);
        Assert.Equal(wet.Status == "aligned", after != (exists ? before : string.Empty));

        Assert.True((await subject.InspectAsync(repo, cancellationToken: Token)).IsAligned);
        Assert.Equal("already_configured", (await subject.AlignAsync(repo, cancellationToken: Token)).Status);
        Assert.Equal(after, File.Exists(file) ? await File.ReadAllTextAsync(file, Token) : string.Empty);
    }
}
