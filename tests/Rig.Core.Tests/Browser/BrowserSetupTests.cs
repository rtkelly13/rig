using Rig.Core.Browser;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Browser;

/// <summary>agent-browser setup; ported from <c>tests/test_agent_browser_setup.py</c>. Homes are temp folders and the daemon probe is a fake.</summary>
public sealed class BrowserSetupTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly FakeProc _proc = new();
    private readonly Dictionary<string, string> _process = [];
    private readonly List<string> _said = [];

    public void Dispose() => _home.Dispose();

    private BrowserPlaces Places(bool windows = false) => new(_home.Path, "/bin/zsh", windows);

    private BrowserSetup Setup(IUserEnvironment? store = null, bool windows = false) =>
        new(Places(windows), n => _process.GetValueOrDefault(n), store ?? new ShellProfileEnvironment(Places().ShellProfile), _proc);

    private string Zshrc => Path.Join(_home.Path, ".zshrc");

    private static string Text(string path) => File.ReadAllText(path);

    private static void Put(string path, string text) => File.WriteAllText(path, text);

    private Task<BrowserEnvOutcome> ApplyAsync(bool dryRun = false, bool force = false, IUserEnvironment? store = null, bool windows = false) =>
        Setup(store, windows).ApplyEnvironmentAsync(dryRun, force, _said.Add, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("/bin/zsh", ".zshrc")]
    [InlineData("/usr/bin/bash", ".bashrc")]
    [InlineData(null, ".bashrc")]
    public void The_shell_picks_the_profile(string? shell, string file)
    {
        Assert.Equal(Path.Join("/h", file), new BrowserPlaces("/h", shell, false).ShellProfile);
    }

    [Fact]
    public void Places_name_the_profiles_and_shortcut_files()
    {
        var places = new BrowserPlaces("/h", null, true);

        Assert.Equal(Path.Join("/h", ".agent-browser-profiles", "dev"), places.DevProfile);
        Assert.Equal(Path.Join("/h", "Documents", "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"), places.ShortcutsProfile);
        Assert.Equal(Path.Join("/h", ".zshrc"), (places with { Windows = false }).ShortcutsProfile);
    }

    [Fact]
    public async Task Setup_writes_every_unset_variable_to_the_shell_profile()
    {
        var outcome = await ApplyAsync();

        Assert.Equal(new BrowserEnvOutcome(3, 0), outcome);
        var dev = Places().DevProfile;
        Assert.Equal(
            $"\nexport AGENT_BROWSER_AUTO_CONNECT=\"0\"\n\nexport AGENT_BROWSER_HEADED=\"1\"\n\nexport AGENT_BROWSER_PROFILE=\"{dev}\"\n",
            Text(Zshrc));
        Assert.Equal(dev, Setup().Current(BrowserSetup.ProfileVariable));
        Assert.Contains("  + AGENT_BROWSER_HEADED=1", _said);
        Assert.Contains("✓ Applied 3 agent-browser environment setting(s). Open a new terminal for them to take effect.", _said);
        Assert.StartsWith("Dedicated profile is the default.", _said[^1], StringComparison.Ordinal);
        Assert.Equal(["pgrep", "-f", "agent-browser"], Assert.Single(_proc.Requests).Argv);
    }

    [Fact]
    public async Task Setup_never_clobbers_and_a_second_run_changes_nothing()
    {
        _process["AGENT_BROWSER_HEADED"] = "0";

        Assert.Equal(new BrowserEnvOutcome(2, 1), await ApplyAsync());
        var written = Text(Zshrc);
        Assert.DoesNotContain("AGENT_BROWSER_HEADED", written, StringComparison.Ordinal);

        _said.Clear();
        Assert.Equal(new BrowserEnvOutcome(0, 3), await ApplyAsync());
        Assert.Equal(written, Text(Zshrc));
        Assert.Equal("✓ agent-browser environment already configured.", _said[0]);
    }

    [Fact]
    public async Task Force_rewrites_variables_already_set()
    {
        _process[BrowserSetup.ProfileVariable] = "/elsewhere";

        Assert.Equal(new BrowserEnvOutcome(3, 0), await ApplyAsync(force: true));
        Assert.Contains($"export {BrowserSetup.ProfileVariable}=", Text(Zshrc), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_reports_the_plan_and_writes_nothing()
    {
        Assert.Equal(new BrowserEnvOutcome(0, 0), await ApplyAsync(dryRun: true));

        Assert.False(File.Exists(Zshrc));
        Assert.Equal("Dry run — no changes written.", _said[^1]);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task The_old_auto_connect_default_is_flagged_not_changed()
    {
        Put(Zshrc, "export AGENT_BROWSER_AUTO_CONNECT=1\n");

        Assert.Equal(new BrowserEnvOutcome(2, 1), await ApplyAsync());

        Assert.StartsWith("⚠ AGENT_BROWSER_AUTO_CONNECT is already set to '1'", _said[0], StringComparison.Ordinal);
        Assert.StartsWith("export AGENT_BROWSER_AUTO_CONNECT=1\n", Text(Zshrc), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_daemon_is_told_to_restart()
    {
        _proc.Answer(new Ran(0, "4242\n", string.Empty), "pgrep");

        await ApplyAsync();

        Assert.Contains(_said, s => s.StartsWith("⚠ An agent-browser daemon is already running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windows_probes_with_powershell()
    {
        _proc.Answer(new Ran(0, "  \n", string.Empty), "powershell");

        Assert.False(await Setup(windows: true).DaemonRunningAsync(TestContext.Current.CancellationToken));
        Assert.Equal("powershell", Assert.Single(_proc.Requests).Argv[0]);
    }

    [Theory]
    [InlineData(false, "✗ Failed to update shell profile: refused")]
    [InlineData(true, "✗ Failed to set AGENT_BROWSER_AUTO_CONNECT: refused")]
    public async Task A_failed_write_is_reported_and_not_counted(bool windows, string message)
    {
        var outcome = await ApplyAsync(store: new RefusingEnvironment(), windows: windows);

        Assert.Equal(new BrowserEnvOutcome(0, 0), outcome);
        Assert.Contains(message, _said);
        Assert.DoesNotContain(_said, s => s.StartsWith("✓ Applied", StringComparison.Ordinal));
    }

    [Fact]
    public void Profiles_are_created_and_shortcuts_added_once()
    {
        File.WriteAllText(Zshrc, "# existing\n");

        Setup().ProvisionProfiles(dryRun: false, _said.Add);
        Setup().ProvisionProfiles(dryRun: false, _said.Add);

        Assert.True(Directory.Exists(Path.Join(_home.Path, ".agent-browser-profiles", "dev")));
        Assert.True(Directory.Exists(Path.Join(_home.Path, ".agent-browser-profiles", "personal")));
        var text = File.ReadAllText(Zshrc);
        Assert.Equal(1, text.Split(BrowserSetup.ShortcutsMarker).Length - 1);
        Assert.Contains("--profile $HOME/.agent-browser-profiles/dev", text, StringComparison.Ordinal);
        Assert.Contains("--profile $HOME/.agent-browser-profiles/personal", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BrowserSetup.ProfileVariable, text, StringComparison.Ordinal);
        Assert.Single(_said, "✓ Injected Agent Browser shell helpers into .zshrc");
    }

    [Fact]
    public void Windows_shortcuts_go_in_an_existing_powershell_profile()
    {
        var profile = _home.File("Documents/WindowsPowerShell/Microsoft.PowerShell_profile.ps1");
        File.WriteAllText(profile, string.Empty);

        Setup(windows: true).ProvisionProfiles(dryRun: false, _said.Add);

        Assert.Contains("function agent-browser-dev", File.ReadAllText(profile), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_shell_profile_only_the_folders_are_made()
    {
        Setup().ProvisionProfiles(dryRun: false, _said.Add);

        Assert.False(File.Exists(Zshrc));
        Assert.Equal([$"✓ Agent-browser profiles initialized in {Places().ProfilesRoot}"], _said);
    }

    [Fact]
    public void A_dry_run_makes_no_profiles()
    {
        Setup().ProvisionProfiles(dryRun: true, _said.Add);

        Assert.False(Directory.Exists(Places().ProfilesRoot));
        Assert.Equal([$"Dry run: would ensure {Places().ProfilesRoot} profiles exist"], _said);
    }

    [Fact]
    public void An_unwritable_shell_profile_is_reported()
    {
        File.WriteAllText(Zshrc, "# existing\n");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(Zshrc, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(Zshrc, UnixFileMode.UserRead);
        }

        Setup().ProvisionProfiles(dryRun: false, _said.Add);

        Assert.StartsWith("Could not update shell profile for agent browser: ", _said[^1], StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(Zshrc, FileAttributes.Normal);
        }
    }

    [Fact]
    public void The_shell_store_reads_quoted_exports_and_misses_cleanly()
    {
        var store = new ShellProfileEnvironment(Zshrc);
        Assert.Null(store.Read("X"));

        File.WriteAllText(Zshrc, "  export X=\"one\"  \nexport X=two\n");

        Assert.Equal("one", store.Read("X"));
        Assert.Null(store.Read("Y"));
    }

    private sealed class RefusingEnvironment : IUserEnvironment
    {
        public string? Read(string name) => null;

        public void Write(string name, string value) => throw new IOException("refused");
    }
}
