using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Processes;

public sealed class GitTests : IDisposable
{
    private readonly TempDir _root = new TempDir().AsCodeRoot();
    private readonly FakeProc _proc = new FakeProc().Answer(new Ran(0, " main \n feature \n", string.Empty), "git");

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData("external/skills/x")]
    [InlineData("other/dotnet")]
    public async Task Hardens_calls_under_untrusted_folders(string clone)
    {
        var cwd = _root.Folder(clone);

        await new Git(_proc, _root.Path).RunAsync(cwd, ["status"], cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(_proc.Requests);
        Assert.Equal(["git", .. Git.HardeningArgs, "status"], request.Argv);
        Assert.Equal("0", request.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(cwd, request.WorkingDirectory);
        Assert.Equal(Git.DefaultTimeout, request.Timeout);
    }

    [Fact]
    public async Task Leaves_trusted_calls_alone_and_keeps_the_callers_environment()
    {
        var cwd = _root.Folder("personal/rig");
        var env = new Dictionary<string, string> { ["GIT_DIR"] = ".git" };

        await new Git(_proc, _root.Path).RunAsync(cwd, ["status"], TimeSpan.FromSeconds(5), env, "in", TestContext.Current.CancellationToken);

        var request = Assert.Single(_proc.Requests);
        Assert.Equal(["git", "status"], request.Argv);
        Assert.Equal(env, request.Environment);
        Assert.Equal(TimeSpan.FromSeconds(5), request.Timeout);
        Assert.Equal("in", request.StdinText);
        Assert.DoesNotContain("GIT_DIR", request.Unset);
        Assert.Contains("GIT_WORK_TREE", request.Unset);
    }

    [Fact]
    public async Task Keeps_the_callers_environment_when_hardening()
    {
        var cwd = _root.Folder("external/x");

        await new Git(_proc, _root.Path).RunAsync(cwd, ["fetch"], environment: new Dictionary<string, string> { ["A"] = "1" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("1", Assert.Single(_proc.Requests).Environment["A"]);
    }

    [Fact]
    public void A_symlinked_untrusted_folder_is_still_untrusted()
    {
        using var elsewhere = new TempDir();
        var clone = Directory.CreateDirectory(Path.Combine(elsewhere.Path, "skills", "x")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_root.Path, "external"), elsewhere.Path);

        Assert.True(new Git(_proc, _root.Path).IsUntrusted(Path.Combine(_root.Path, "external", "skills", "x")));
        Assert.True(new Git(_proc, _root.Path).IsUntrusted(clone));
    }

    [Fact]
    public void A_missing_working_directory_means_the_current_one()
    {
        var git = new Git(_proc, _root.Path);

        Assert.False(git.IsUntrusted(null));
        Assert.True(new Git(_proc, Directory.GetCurrentDirectory()).IsUntrusted(Path.Combine(Directory.GetCurrentDirectory(), "external")));
    }

    [Fact]
    public async Task Out_and_lines_read_what_git_prints()
    {
        var git = new Git(_proc, _root.Path);

        Assert.Equal("main \n feature", await git.OutAsync(_root.Path, "branch"));
        Assert.Equal(["main", "feature"], (await git.LinesAsync(_root.Path, "branch"))!);
    }
}
