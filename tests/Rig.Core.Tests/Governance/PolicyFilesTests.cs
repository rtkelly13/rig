using System.Text.Json.Nodes;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>The AI policy, security policy and README disclosure, over a temp checkout and a faked gh.</summary>
public sealed class PolicyFilesTests : IDisposable
{
    private static readonly RepoMetadata Meta = new("o/r", true, "MIT", "sec@example.com", "Python 3.11+");

    private static readonly string Section = PolicyFiles.Render("README_AI_SECTION.md.jinja", Meta);

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Repo => _root.Folder("repo");

    private PolicyFiles Files => new(_proc, new Git(_proc, _root.Path));

    [Fact]
    public void The_readme_section_renders_without_its_final_newline()
    {
        Assert.StartsWith("## 🤖 AI Utilization & Transparency\n\nThis project leverages", Section, StringComparison.Ordinal);
        Assert.Contains("automated test suites (Python 3.11+), strict type checking", Section, StringComparison.Ordinal);
        Assert.EndsWith("see [**`AI_POLICY.md`**](AI_POLICY.md).", Section, StringComparison.Ordinal);
    }

    [Fact]
    public void The_policies_are_written_trimmed_with_one_final_newline()
    {
        PolicyFiles.ApplyAiPolicy(Repo, Meta, dryRun: false);
        PolicyFiles.ApplySecurityPolicy(Repo, Meta, dryRun: false);

        var ai = File.ReadAllText(Path.Join(Repo, "AI_POLICY.md"));
        Assert.StartsWith("# Generative AI & AI-Assisted Contribution Policy\n", ai, StringComparison.Ordinal);
        Assert.Contains("submitted to `o/r`.", ai, StringComparison.Ordinal);
        Assert.Contains("all supported runtimes (Python 3.11+).", ai, StringComparison.Ordinal);
        Assert.EndsWith("under the project's [MIT License](LICENSE).\n", ai, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", ai, StringComparison.Ordinal);

        var security = File.ReadAllText(Path.Join(Repo, "SECURITY.md"));
        Assert.Contains("We take the security of `o/r` seriously.", security, StringComparison.Ordinal);
        Assert.Contains("email the maintainer at `sec@example.com`.", security, StringComparison.Ordinal);
        Assert.EndsWith("work on a fix promptly.\n", security, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dry_run_writes_nothing()
    {
        File.WriteAllText(Path.Join(Repo, "README.md"), "# T\n");

        PolicyFiles.ApplyAiPolicy(Repo, Meta, dryRun: true);
        PolicyFiles.ApplySecurityPolicy(Repo, Meta, dryRun: true);
        Assert.True(PolicyFiles.InjectReadmeAiDisclosure(Repo, Meta, dryRun: true));

        Assert.Equal(["README.md"], Directory.GetFiles(Repo).Select(Path.GetFileName));
        Assert.Equal("# T\n", File.ReadAllText(Path.Join(Repo, "README.md")));
    }

    [Fact]
    public void Without_a_readme_there_is_nothing_to_disclose_in() =>
        Assert.False(PolicyFiles.InjectReadmeAiDisclosure(Repo, Meta, dryRun: false));

    [Fact]
    public void The_badge_follows_the_last_header_badge_and_the_section_precedes_every_license_heading()
    {
        var readme = Inject("# T\n\n[![A](a)](a)\n[![B](b)](b)\n\nBody\n\n## 📄 License\n\nMIT\n\n## 📄 License\n");

        // Rejoining the lines drops the final newline; every License heading gains the section.
        var before = $"{Section}\n\n---\n\n## 📄 License";
        Assert.Equal($"# T\n\n[![A](a)](a)\n[![B](b)](b)\n{PolicyFiles.Badge}\n\nBody\n\n{before}\n\nMIT\n\n{before}", readme);
    }

    [Fact]
    public void Without_badges_or_a_license_the_badge_leads_and_the_section_closes()
    {
        var readme = Inject("# T\n\nBody\n\n\n");

        Assert.Equal($"{PolicyFiles.Badge}\n\n# T\n\nBody\n\n---\n\n{Section}\n", readme);
    }

    [Fact]
    public void A_plain_license_heading_and_a_spaced_badge_are_recognised()
    {
        var readme = Inject("# T\n[! [x](y)](z)\n\n## License\nMIT\n");

        Assert.Equal($"# T\n[! [x](y)](z)\n{PolicyFiles.Badge}\n\n{Section}\n\n---\n\n## License\nMIT", readme);
    }

    [Fact]
    public void Windows_line_endings_are_read_as_newlines()
    {
        var readme = Inject("# T\r\n\r\n[![A](a)](a)\r\nBody\r\n");

        Assert.Equal($"# T\n\n[![A](a)](a)\n{PolicyFiles.Badge}\nBody\n\n---\n\n{Section}\n", readme);
    }

    [Fact]
    public void Only_badges_in_the_first_fifteen_lines_count()
    {
        var lines = string.Concat(Enumerable.Range(0, 15).Select(i => $"line {i}\n"));

        var readme = Inject($"{lines}[![Late](l)](l)\n## License\n");

        Assert.StartsWith($"{PolicyFiles.Badge}\n\nline 0\n", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Injecting_twice_changes_nothing_more()
    {
        var once = Inject("# T\n\n[![A](a)](a)\n\n## 📄 License\n\nMIT");

        Assert.Equal(once, Inject(once));
    }

    [Fact]
    public async Task Without_gh_the_folder_name_a_private_repo_and_the_policy_contact_are_assumed()
    {
        var policies = RepoPolicies.Parse(new JsonObject { ["defaults"] = new JsonObject { ["contact_email"] = "security@example.com" } }, "policy.json");
        _ = _proc.Answer(new Ran(1, string.Empty, "gh: not a repo"), "gh");

        var meta = await Files.DetectAsync(Repo, policies, Token);

        Assert.Equal(new RepoMetadata("repo", false, "MIT", "security@example.com", ".NET 8 & .NET 9"), meta);
    }

    [Fact]
    public async Task Gh_is_asked_about_origin_rather_than_an_upstream_remote()
    {
        var policies = RepoPolicies.Parse(
            new JsonObject { ["repo_overrides"] = new JsonObject { ["parquet-dotnet"] = new JsonObject { ["contact_email"] = "fork@example.com" } } }, "policy.json");
        const string Origin = "https://github.com/rtkelly-labs/parquet-dotnet.git";
        _ = _proc.Answer(new Ran(0, $"{Origin}\n", string.Empty), "git", "remote", "get-url", "origin")
            .Answer(new Ran(0, """{"nameWithOwner": "rtkelly-labs/parquet-dotnet", "isPrivate": false, "licenseInfo": {"spdxId": "Apache-2.0"}}""", string.Empty), "gh");

        var meta = await Files.DetectAsync(Repo, policies, Token);

        var gh = Assert.Single(_proc.Requests, r => r.Argv[0] == "gh");
        Assert.Equal(["gh", "repo", "view", Origin, "--json", "nameWithOwner,isPrivate,visibility,licenseInfo"], gh.Argv);
        Assert.Equal(Repo, gh.WorkingDirectory);
        Assert.Equal(new RepoMetadata("rtkelly-labs/parquet-dotnet", true, "Apache-2.0", "fork@example.com", ".NET 8 & .NET 9"), meta);
    }

    [Fact]
    public async Task Unreadable_gh_output_falls_back_like_a_failure()
    {
        _ = _proc.Answer(new Ran(0, "not json", string.Empty), "gh");

        var meta = await Files.DetectAsync(Repo, RepoPolicies.Empty, Token);

        Assert.Equal(("repo", false, "MIT"), (meta.RepoName, meta.IsPublic, meta.License));
    }

    [Theory]
    [InlineData(new[] { "pyproject.toml" }, "Python 3.11+")]
    [InlineData(new[] { "setup.py" }, "Python 3.11+")]
    [InlineData(new[] { "package.json" }, "Node.js 20+ / TypeScript")]
    [InlineData(new[] { "package.json", "pyproject.toml" }, "Python 3.11+")]
    [InlineData(new[] { "x.csproj" }, ".NET 8 & .NET 9")]
    public void Runtimes_are_guessed_from_the_root(string[] files, string expected)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Join(Repo, file), string.Empty);
        }

        Assert.Equal(expected, PolicyFiles.Runtimes(Repo));
    }

    private string Inject(string readme)
    {
        File.WriteAllText(Path.Join(Repo, "README.md"), readme);
        Assert.True(PolicyFiles.InjectReadmeAiDisclosure(Repo, Meta, dryRun: false));
        return File.ReadAllText(Path.Join(Repo, "README.md"));
    }
}
