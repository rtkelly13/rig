using Rig.Core.Forks;
using Rig.Core.Processes;
using Rig.Core.Tests.Estate;

namespace Rig.Core.Tests.Forks;

/// <summary>The reader, against real git repositories.</summary>
public sealed class ForkFactsReaderTests : IDisposable
{
    private static readonly string[] SignOff = ["--signoff"];

    private readonly TempDir _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static ForkFactsReader Reader() => new(new Git(Repos.Proc, Path.GetTempPath()));

    private static async Task WriteAsync(string repo, string path, string text)
    {
        var full = Path.Join(repo, path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, text, Token);
    }

    private static async Task CommitAsAsync(string repo, string name, string email, string message, bool signOff = false)
    {
        _ = await Repos.GitAsync(repo, "add", "-A");
        _ = await Repos.GitAsync(
            repo,
            ["-c", $"user.name={name}", "-c", $"user.email={email}", "commit", "-q", "--allow-empty", "-m", message, .. signOff ? SignOff : []]);
    }

    private async Task<string> UpstreamAsync()
    {
        var repo = await Repos.InitAsync(_temp.Folder("up"));
        await WriteAsync(repo, "LICENSE", "MIT License\n\nCopyright (c) 2020 Jo Upstream\n\nPermission is hereby granted, free of charge, to any person obtaining a copy\n");
        await WriteAsync(repo, "src/Lib/Lib.csproj", "<Project><PropertyGroup><PackageId>Upstream.Lib</PackageId></PropertyGroup></Project>\n");
        await WriteAsync(repo, "src/Lib/Code.cs", "class A\n{\n}\n");
        await WriteAsync(repo, "src/Lib/Meta.thrift", "// Licensed under the Apache License, Version 2.0\nstruct S {}\n");
        await WriteAsync(repo, "src/Lib/Spdx.cs", "// SPDX-License-Identifier: BSD-3-Clause\nclass B {}\n");
        await WriteAsync(repo, "src/Lib/bin/Debug/LICENSE.txt", "ignored\n");
        await CommitAsAsync(repo, "Jo Upstream", "jo@example.invalid", "code", signOff: true);
        await WriteAsync(repo, "src/Lib/Code.cs", "class A\n{\n    int x;\n}\n");
        await CommitAsAsync(repo, "Sam", "sam@example.invalid", "field");
        _ = await Repos.GitAsync(repo, "checkout", "-q", "-b", "side");
        await CommitAsAsync(repo, "Jo Upstream", "jo@example.invalid", "side work");
        _ = await Repos.GitAsync(repo, "checkout", "-q", "main");
        _ = await Repos.GitAsync(repo, "tag", "v1");
        return repo;
    }

    [Fact]
    public async Task It_reads_history_files_markers_and_unmerged_refs_from_one_revision()
    {
        var repo = await UpstreamAsync();
        // A change in the working tree never reaches the report: files are read from the revision.
        await WriteAsync(repo, "LICENSE", "changed");

        var facts = await Reader().ReadAsync(repo, "main", ForkAssessmentTests.Shipped, null, blame: false, [], Token);

        Assert.Equal(await Repos.GitAsync(repo, "rev-parse", "main"), facts.Revision);
        Assert.False(facts.Shallow);
        Assert.Equal(3, facts.Commits.Count);
        Assert.Equal(1, facts.Commits.Count(c => c.SignedOff));
        Assert.Equal(1, facts.Tags);
        Assert.DoesNotContain(facts.Files, f => f.Contains("/bin/", StringComparison.Ordinal));
        Assert.StartsWith("MIT License", facts.Texts["LICENSE"], StringComparison.Ordinal);
        Assert.Contains("src/Lib/Lib.csproj", facts.Texts.Keys);
        Assert.Contains(new HeaderHit("src/Lib/Meta.thrift", "Apache-2.0"), facts.HeaderHits);
        Assert.Contains(new HeaderHit("src/Lib/Spdx.cs", "BSD-3-Clause"), facts.HeaderHits);
        Assert.Equal("side", Assert.Single(facts.Refs).Name);
        Assert.Null(facts.Blame);
    }

    [Fact]
    public async Task Blame_and_a_mailmap_attribute_lines_to_merged_people()
    {
        var repo = await UpstreamAsync();
        var mailmap = Path.Join(_temp.Path, "mailmap");
        await File.WriteAllTextAsync(mailmap, "Jo Upstream <jo@example.invalid> Sam <sam@example.invalid>\n", Token);

        var facts = await Reader().ReadAsync(repo, "main", ForkAssessmentTests.Shipped, mailmap, blame: true, ["refs/heads/main"], Token);

        Assert.All(facts.Commits.Where(c => c.Name != "t"), c => Assert.Equal("Jo Upstream", c.Name));
        Assert.Empty(facts.Refs);
        var lines = Assert.Single(facts.Blame!);
        Assert.Equal(("Jo Upstream", 6), (lines.Name, lines.Lines));
    }

    [Fact]
    public async Task The_same_revision_always_gives_the_same_report()
    {
        var repo = await UpstreamAsync();

        // Refs are live state, so the report names the ones it searched; pinned to side, nothing here moves.
        async Task<string> ReportAsync() =>
            ForkAssessment.Assess(ForkAssessmentTests.Shipped, await Reader().ReadAsync(repo, "v1", ForkAssessmentTests.Shipped, null, blame: true, ["refs/heads/side"], Token)).ToJson();

        var first = await ReportAsync();
        await CommitAsAsync(repo, "Kim", "kim@example.invalid", "later");

        Assert.Equal(first, await ReportAsync());
    }

    [Fact]
    public async Task A_revision_that_does_not_exist_is_an_error()
    {
        var repo = await UpstreamAsync();

        _ = await Assert.ThrowsAsync<ProcException>(() => Reader().ReadAsync(repo, "nope", ForkAssessmentTests.Shipped, null, blame: false, [], Token));
    }

    [Fact]
    public void Commit_records_parse_names_dates_and_sign_offs()
    {
        var commits = ForkFactsReader.Commits("\u001eJo\u001fjo@x.invalid\u001f2024-02-03\u001fJo <jo@x.invalid>\n\u001eSam\u001fsam@x.invalid\u001f2023-01-01\u001f\n");

        Assert.Equal(
            [new CommitFact("Jo", "jo@x.invalid", new DateOnly(2024, 2, 3), true), new CommitFact("Sam", "sam@x.invalid", new DateOnly(2023, 1, 1), false)],
            commits);
    }
}
