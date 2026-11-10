using System.CommandLine;
using Rig.Cli.Forwarding;
using Rig.Cli.Vault;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Vault;

/// <summary>Ported from the Python <c>md-lint</c> entry point, which had only an import smoke test.</summary>
public sealed class MdLintCommandTests : IDisposable
{
    private const string Checkout = "/rig";

    private readonly TempDir _home = new();
    private readonly FakeProc _proc = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _home.Dispose();
    }

    private string DefaultVault => Path.Combine(_home.Path, "code", "personal", "obsidian-vault", "Vault");

    private MdLintCommand Command() => new(_proc, new MdLintHost(Checkout, v => _environment.GetValueOrDefault(v), _home.Path), _err);

    private Task<int> Run(params string[] args) => Command().Build().Parse(args).InvokeAsync(
        new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);

    private static string[] Mdformat(params string[] args) => ["uv", "run", "--no-sync", "--project", Checkout, "mdformat", .. args];

    [Fact]
    public async Task With_no_paths_it_checks_the_vault_with_rigs_own_mdformat()
    {
        _home.Folder("code/personal/obsidian-vault/Vault");
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "uv");

        Assert.Equal(0, await Run("md-lint"));

        var request = Assert.Single(_proc.Requests);
        Assert.Equal(Mdformat("--check", DefaultVault), request.Argv);
        Assert.True(request.Attached);
        Assert.Null(request.WorkingDirectory);
    }

    [Fact]
    public async Task Fix_rewrites_the_named_paths_and_passes_other_flags_to_mdformat()
    {
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "uv");

        Assert.Equal(0, await Run("md-lint", "a.md", "--fix", "docs", "--wrap", "80"));

        Assert.Equal(Mdformat("a.md", "docs", "--wrap", "80"), Assert.Single(_proc.Requests).Argv);
    }

    [Fact]
    public async Task Mdformats_exit_code_is_returned_unchanged()
    {
        _proc.Answer(new Ran(1, string.Empty, string.Empty), "uv");

        Assert.Equal(1, await Run("md-lint", "note.md"));
    }

    [Theory]
    [InlineData("VAULT_PATH")]
    [InlineData("TVS_VAULT_PATH")]
    public async Task The_vault_can_be_overridden_and_a_missing_one_is_an_error(string variable)
    {
        _environment[variable] = "~/elsewhere/Vault";

        Assert.Equal(1, await Run("md-lint"));

        Assert.Contains(
            $"Vault not found at {Path.Combine(_home.Path, "elsewhere/Vault")} — set VAULT_PATH to override.",
            _err.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Without_uv_it_says_where_mdformat_comes_from()
    {
        _proc.Unanswered = new Ran(Ran.NotFound, string.Empty, "uv is not on PATH");

        Assert.Equal(2, await Run("md-lint", "note.md"));

        Assert.Contains("rig md-lint: uv is not on PATH. mdformat runs from rig's uv environment.", _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/v", "v")]
    public void A_leading_tilde_is_the_home_folder(string path, string under) =>
        Assert.Equal(Path.Combine(_home.Path, under), VaultPath.ExpandHome(path, _home.Path));

    [Fact]
    public void Other_paths_are_kept_as_they_are() => Assert.Equal("/x/~y", VaultPath.ExpandHome("/x/~y", _home.Path));

    [Fact]
    public async Task Rig_serves_md_lint_itself()
    {
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "uv");
        var app = new RigApp(
            new PythonRig(_proc, Checkout), new FakeGitHub(), _ => Task.FromResult<RepoSlug?>(null), _out, _err, [Command().Build]);

        Assert.Equal(0, await app.RunAsync(["md-lint", "note.md"], TestContext.Current.CancellationToken));

        Assert.Equal(Mdformat("--check", "note.md"), Assert.Single(_proc.Requests).Argv);
    }
}
