using System.CommandLine;
using Rig.Cli.Browser;
using Rig.Core.Browser;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Browser;

/// <summary><c>rig browser</c>; ported from <c>tests/test_agent_browser_setup.py</c>. Every home is a temp folder.</summary>
public sealed class BrowserCommandTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly FakeProc _proc = new();
    private readonly Dictionary<string, string> _process = [];

    public void Dispose() => _home.Dispose();

    private string Zshrc => Path.Join(_home.Path, ".zshrc");

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new BrowserHost(
            Path.Join(_home.Path, "not-this-home"),
            "/bin/zsh",
            false,
            n => _process.GetValueOrDefault(n),
            places => new ShellProfileEnvironment(places.ShellProfile));
        var command = new BrowserCommand(host, _proc, output, error).Build();
        var code = await command.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    private static string Text(string path) => File.ReadAllText(path);

    [Fact]
    public void Browser_offers_setup_and_profiles_only()
    {
        var command = new BrowserCommand(new BrowserHost("/", null, false, _ => null, _ => new ShellProfileEnvironment("/none")), _proc, TextWriter.Null, TextWriter.Null).Build();

        Assert.Equal(["setup", "profiles"], command.Subcommands.Select(c => c.Name));
    }

    [Fact]
    public async Task Setup_writes_the_variables_into_the_given_home()
    {
        var (code, _, error) = await RunAsync("setup", "--home", _home.Path);

        Assert.Equal(0, code);
        Assert.Contains("✓ Applied 3 agent-browser environment setting(s).", error, StringComparison.Ordinal);
        Assert.Contains("export AGENT_BROWSER_HEADED=\"1\"", Text(Zshrc), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(_home.Path, "not-this-home")));
    }

    [Fact]
    public async Task Setup_says_how_many_were_left_alone()
    {
        _process["AGENT_BROWSER_HEADED"] = "0";

        var (_, _, error) = await RunAsync("setup", "--home", _home.Path);

        Assert.EndsWith("1 var(s) already set — left untouched.\n", error.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_setup_writes_nothing()
    {
        _process["AGENT_BROWSER_HEADED"] = "0";

        var (code, _, error) = await RunAsync("setup", "--dry-run", "--home", _home.Path);

        Assert.Equal(0, code);
        Assert.False(File.Exists(Zshrc));
        Assert.DoesNotContain("left untouched", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profiles_creates_the_folders_in_the_given_home()
    {
        var (code, output, _) = await RunAsync("profiles", "--home", _home.Path);

        Assert.Equal(0, code);
        Assert.True(Directory.Exists(Path.Join(_home.Path, ".agent-browser-profiles", "personal")));
        Assert.StartsWith("✓ Agent-browser profiles initialized in ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_profiles_makes_nothing()
    {
        var (_, output, _) = await RunAsync("profiles", "--dry-run", "--home", _home.Path);

        Assert.StartsWith("Dry run: would ensure ", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(_home.Path, ".agent-browser-profiles")));
    }
}
