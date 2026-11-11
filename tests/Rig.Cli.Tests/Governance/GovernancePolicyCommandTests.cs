using System.CommandLine;
using Rig.Cli.Governance;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Governance;

/// <summary>
/// <c>rig repo-governance apply-policy</c> and <c>sync-policies</c> end to end over temp clones, with gh faked: the
/// working directory is a clone inside the estate folder, so siblings are the other clones.
/// </summary>
public sealed class GovernancePolicyCommandTests : IDisposable
{
    private const string Public = """{"nameWithOwner": "o/lib", "isPrivate": false}""";

    private const string Private = """{"nameWithOwner": "o/lib", "isPrivate": true}""";

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public GovernancePolicyCommandTests()
    {
        _ = _root.Folder("estate/rig");
        File.WriteAllText(
            _root.File($"checkout/config/{RepoPolicies.FileName}"),
            """
            {"profiles": {
              "public_open_source": {"repos": ["lib", "absent"], "policies": ["ai_policy", "readme_ai_disclosure", "security_policy"]},
              "private_workspace": {"repos": ["tool"], "policies": ["squash_merge_only", "ai_policy"]}
            }}
            """);
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Cwd => Path.Join(_root.Path, "estate", "rig");

    [Fact]
    public async Task Apply_policy_writes_every_policy_to_a_public_sibling_named_by_folder()
    {
        var lib = Clone("lib", readme: "# Lib\n");
        Gh(Public);

        var (code, output) = await RunAsync("apply-policy", "--repo", "lib");

        Assert.Equal(0, code);
        Assert.Equal(
            $"""
            {GovernanceCommand.Panel("Applying Governance Policies to o/lib")}Visibility: Public | Runtimes: .NET 8 & .NET 9
              ✓ AI_POLICY.md and README.md disclosure applied
              ✓ SECURITY.md applied

            ✓ Policy application complete!

            """.ReplaceLineEndings("\n"),
            output);
        Assert.True(File.Exists(Path.Join(lib, "AI_POLICY.md")));
        Assert.True(File.Exists(Path.Join(lib, "SECURITY.md")));
        Assert.Contains(PolicyFiles.SectionHeading, await File.ReadAllTextAsync(Path.Join(lib, "README.md"), Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_private_repo_gets_no_security_policy()
    {
        var lib = Clone("lib");
        Gh(Private);

        var (code, output) = await RunAsync("apply-policy", "--repo", "lib");

        Assert.Equal(0, code);
        Assert.Contains("Visibility: Private", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SECURITY.md", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(lib, "SECURITY.md")));
        Assert.True(File.Exists(Path.Join(lib, "AI_POLICY.md")));
    }

    [Fact]
    public async Task Policy_security_skips_the_ai_policy_and_a_dry_run_writes_nothing()
    {
        var lib = Clone("lib");
        Gh(Public);

        var (code, output) = await RunAsync("apply-policy", "-p", "security", "-r", lib, "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("  ✓ SECURITY.md applied\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("AI_POLICY", output, StringComparison.Ordinal);
        Assert.Equal([".git"], Directory.GetFileSystemEntries(lib).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Apply_policy_defaults_to_the_working_directory()
    {
        _ = _root.Folder("estate/rig/.git");
        Gh(Private);

        var (code, _) = await RunAsync("apply-policy");

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Join(Cwd, "AI_POLICY.md")));
    }

    [Fact]
    public async Task A_folder_that_is_not_a_clone_is_refused()
    {
        _ = _root.Folder("estate/plain");

        var (code, output) = await RunAsync("apply-policy", "--repo", "plain");

        Assert.Equal(1, code);
        Assert.Equal($"❌ Directory {Path.Join(_root.Path, "estate", "plain")} is not a valid git repository.\n", output);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Sync_policies_walks_the_public_profile_and_skips_absent_clones()
    {
        var lib = Clone("lib", readme: "# Lib\n");
        _ = Clone("tool");
        Gh(Public);

        var (code, output) = await RunAsync("sync-policies", "--dry-run");

        Assert.Equal(0, code);
        Assert.Equal(
            $"""

            ── Syncing Profile: public_open_source ──
              • lib (Public)
                ✓ AI_POLICY.md
                ✓ README.md AI Section
                ✓ SECURITY.md
              Skipping absent (not found locally at {Path.Join(_root.Path, "estate", "absent")})

            ✓ Multi-repo policy synchronization complete!

            """.ReplaceLineEndings("\n"),
            output);
        Assert.Equal([".git", "README.md"], Directory.GetFileSystemEntries(lib).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("# Lib\n", await File.ReadAllTextAsync(Path.Join(lib, "README.md"), Token));
    }

    [Fact]
    public async Task Sync_policies_all_covers_every_profile_and_writes_only_the_listed_files()
    {
        var lib = Clone("lib");
        var tool = Clone("tool");
        Gh(Private);

        var (code, output) = await RunAsync("sync-policies", "--all");

        Assert.Equal(0, code);
        Assert.Contains("\n── Syncing Profile: private_workspace ──\n  • tool (Private)\n    ✓ AI_POLICY.md\n\n✓", output, StringComparison.Ordinal);

        // The profile, not the visibility, decides: a private clone in the public profile still gets SECURITY.md.
        Assert.True(File.Exists(Path.Join(lib, "SECURITY.md")));
        Assert.Equal([".git", "AI_POLICY.md"], Directory.GetFileSystemEntries(tool).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_working_directory_holding_shared_utilities_is_the_estate_itself()
    {
        _ = _root.Folder("estate/rig/shared-utilities");
        _ = _root.Folder("estate/rig/lib/.git");
        Gh(Public);

        var (_, output) = await RunAsync("sync-policies", "--dry-run");

        Assert.Contains("  • lib (Public)\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_misshapen_policy_is_refused()
    {
        var policy = Path.Join(_root.Path, "checkout", "config", RepoPolicies.FileName);
        await File.WriteAllTextAsync(policy, """{"profiles": ["not", "a", "mapping"]}""", Token);

        var (code, output) = await RunAsync("sync-policies");

        Assert.Equal(1, code);
        Assert.Equal($"❌ Invalid policy config {policy}: {policy}: profiles is not an object\n", output);
    }

    private string Clone(string name, string? readme = null)
    {
        var clone = _root.Folder($"estate/{name}");
        _ = Directory.CreateDirectory(Path.Join(clone, ".git"));
        if (readme is not null)
        {
            File.WriteAllText(Path.Join(clone, "README.md"), readme);
        }

        return clone;
    }

    private void Gh(string view) => _proc.Answer(new Ran(0, view, string.Empty), "gh", "repo", "view");

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var command = new GovernanceCommand(new GovernanceHost(_root.Path, Cwd, Path.Join(_root.Path, "checkout")), _proc, TimeProvider.System, output);
        var code = await new RootCommand { command.Build() }.Parse(["repo-governance", .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = TextWriter.Null }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }
}
