using System.Text;
using System.Xml.Linq;
using Rig.Core.Jobs;
using Rig.Core.Processes;
using Rig.Core.Services;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Jobs;

/// <summary>
/// The macOS LaunchAgents behind <c>rig jobs</c>: what each plist asks launchd to do, and the <c>launchctl</c> calls
/// that install, query and remove them. Nothing here loads a real agent.
/// </summary>
public sealed class JobsLaunchAgentTests : IDisposable
{
    private const string InstallerPath = "/opt/homebrew/bin:/usr/bin:/bin";

    private static readonly JobsInstallTarget Target = new("/checkout", "/checkout/plan.json", "/checkout/temp/jobs-runtime/rig");

    private readonly TempDir temp = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(JobsLaunchAgent.Logon, "logon")]
    [InlineData(JobsLaunchAgent.Tick, "tick")]
    public void Each_agent_runs_the_rig_command_on_its_trigger(string name, string trigger)
    {
        var plist = Plist(name);

        Assert.Equal($"com.rtkelly13.rig.jobs.{trigger}", Text(plist, "Label"));
        Assert.Equal(new[] { Target.Rig, "jobs", "run", "--trigger", trigger, "--scheduled" }, List(plist, "ProgramArguments"));
        Assert.Equal(Target.Root, Text(plist, "WorkingDirectory"));
        Assert.Equal("true", Boolean(plist, "RunAtLoad"));
        Assert.Equal($"/logs/rig-jobs-{trigger}.log", Text(plist, "StandardOutPath"));
        Assert.Equal(Text(plist, "StandardOutPath"), Text(plist, "StandardErrorPath"));
    }

    [Fact]
    public void The_tick_repeats_every_tick_interval_and_the_logon_agent_does_not()
    {
        Assert.Equal($"{Job.TickMinutes * 60}", Text(Plist(JobsLaunchAgent.Tick), "StartInterval"));
        Assert.Null(Entry(Plist(JobsLaunchAgent.Logon).Element("dict")!, "StartInterval"));
    }

    [Theory]
    [InlineData(JobsLaunchAgent.Logon)]
    [InlineData(JobsLaunchAgent.Tick)]
    public void Both_are_background_housekeeping_that_inherits_the_installers_path(string name)
    {
        var plist = Plist(name);

        Assert.Equal("Background", Text(plist, "ProcessType"));
        Assert.Equal("true", Boolean(plist, "LowPriorityIO"));
        Assert.Equal("10", Text(plist, "Nice"));
        Assert.Equal(InstallerPath, Text(plist, "EnvironmentVariables", "PATH"));
    }

    [Fact]
    public void An_installer_without_a_path_leaves_the_environment_alone()
    {
        var plist = XDocument.Parse(Encoding.UTF8.GetString(JobsLaunchAgent.Render(JobsLaunchAgent.Tick, Target, "/logs/x.log", null)));

        Assert.DoesNotContain(plist.Descendants("key"), key => key.Value == "EnvironmentVariables");
    }

    [Fact]
    public void An_agent_that_is_not_one_of_the_two_is_refused() =>
        Assert.Throws<ArgumentException>(() => JobsLaunchAgent.Render("Rig\\Display Guard", Target, "/logs/x.log", InstallerPath));

    [Fact]
    public void The_labels_and_plist_paths_are_the_documented_ones()
    {
        Assert.Equal("com.rtkelly13.rig.jobs.logon", JobsLaunchAgent.Label(JobsLaunchAgent.Logon));
        Assert.Equal("com.rtkelly13.rig.jobs.tick", JobsLaunchAgent.Label(JobsLaunchAgent.Tick));
        Assert.Equal(Path.Join("/agents", "com.rtkelly13.rig.jobs.tick.plist"), JobsLaunchAgent.PlistPath("/agents", JobsLaunchAgent.Tick));
    }

    [Fact]
    public void Macos_installs_two_agents_and_retires_nothing()
    {
        var agents = new LaunchAgents(_ => throw new InvalidOperationException("nothing needs launchd yet"), "logs", null);

        Assert.Equal(JobsPlatform.MacOS, agents.Platform);
        Assert.Equal(new[] { JobsLaunchAgent.Logon, JobsLaunchAgent.Tick }, agents.Names);
        Assert.Empty(agents.Retired);
        Assert.Null(agents.StartsAfterInstall);
    }

    [Fact]
    public async Task Install_writes_the_plist_and_bootstraps_the_agent()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        var launchd = new Launchd(proc, Path.Join(temp.Path, "Library", "LaunchAgents"), "gui/501");
        var agents = new LaunchAgents(_ => Task.FromResult(launchd), Path.Join(temp.Path, "Library", "Logs"), InstallerPath);

        await agents.RegisterAsync(JobsLaunchAgent.Tick, Target, "me", DateTimeOffset.UtcNow, Token);

        var path = Path.Join(launchd.AgentsDir, "com.rtkelly13.rig.jobs.tick.plist");
        var written = XDocument.Load(path);
        Assert.Equal("com.rtkelly13.rig.jobs.tick", Text(written.Root!, "Label"));
        Assert.Equal($"{Job.TickMinutes * 60}", Text(written.Root!, "StartInterval"));
        Assert.Equal(new[] { "launchctl", "bootout", "gui/501", path }, proc.Requests[0].Argv);
        Assert.Equal(new[] { "launchctl", "bootstrap", "gui/501", path }, proc.Requests[1].Argv);
        Assert.True(Directory.Exists(Path.Join(temp.Path, "Library", "Logs")));
    }

    [Fact]
    public async Task Install_writes_one_agent_at_a_time_under_its_own_log()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        var launchd = new Launchd(proc, Path.Join(temp.Path, "Agents"), "gui/501");
        var agents = new LaunchAgents(_ => Task.FromResult(launchd), Path.Join(temp.Path, "Logs"), InstallerPath);

        await agents.RegisterAsync(JobsLaunchAgent.Tick, Target, "me", DateTimeOffset.UtcNow, Token);
        await agents.RegisterAsync(JobsLaunchAgent.Logon, Target, "me", DateTimeOffset.UtcNow, Token);

        var tick = XDocument.Load(Path.Join(launchd.AgentsDir, "com.rtkelly13.rig.jobs.tick.plist")).Root!;
        var logon = XDocument.Load(Path.Join(launchd.AgentsDir, "com.rtkelly13.rig.jobs.logon.plist")).Root!;
        Assert.Equal(Path.Join(temp.Path, "Logs", "rig-jobs-tick.log"), Text(tick, "StandardOutPath"));
        Assert.Equal(Path.Join(temp.Path, "Logs", "rig-jobs-logon.log"), Text(logon, "StandardOutPath"));
        Assert.Null(Entry(logon.Element("dict")!, "StartInterval"));
    }

    [Fact]
    public async Task Status_asks_launchctl_about_the_agent_label()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl", "print");
        var agents = new LaunchAgents(_ => Task.FromResult(new Launchd(proc, "/agents", "gui/501")), "/logs", null);

        Assert.True(await agents.ExistsAsync(JobsLaunchAgent.Logon, Token));
        string[] expected = ["launchctl", "print", "gui/501/com.rtkelly13.rig.jobs.logon"];
        Assert.Equal(expected, Assert.Single(proc.Requests).Argv);
    }

    [Fact]
    public async Task Uninstall_removes_the_plist_and_ignores_an_absent_agent()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        var launchd = new Launchd(proc, Path.Join(temp.Path, "Agents"), "gui/501");
        var agents = new LaunchAgents(_ => Task.FromResult(launchd), "/logs", null);

        Assert.False(await agents.DeleteAsync(JobsLaunchAgent.Tick, Token));
        Assert.Empty(proc.Requests);

        var path = Path.Join(launchd.AgentsDir, "com.rtkelly13.rig.jobs.tick.plist");
        Directory.CreateDirectory(launchd.AgentsDir);
        await File.WriteAllTextAsync(path, "plist", Token);

        Assert.True(await agents.DeleteAsync(JobsLaunchAgent.Tick, Token));
        Assert.False(File.Exists(path));
        Assert.Equal(new[] { "launchctl", "bootout", "gui/501", path }, Assert.Single(proc.Requests).Argv);
    }

    [Fact]
    public async Task The_domain_is_looked_up_once_however_many_agents_are_asked_about()
    {
        var lookups = 0;
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        var agents = new LaunchAgents(
            _ =>
            {
                lookups++;
                return Task.FromResult(new Launchd(proc, "/agents", "gui/501"));
            },
            "/logs",
            null);

        await agents.ExistsAsync(JobsLaunchAgent.Logon, Token);
        await agents.ExistsAsync(JobsLaunchAgent.Tick, Token);

        Assert.Equal(1, lookups);
    }

    private static XElement Plist(string name) =>
        XDocument.Parse(Encoding.UTF8.GetString(JobsLaunchAgent.Render(name, Target, $"/logs/rig-jobs-{JobsLaunchAgent.Wire(name)}.log", InstallerPath))).Root!;

    private static XElement? Entry(XElement dictionary, string key) =>
        dictionary.Elements("key").FirstOrDefault(element => element.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    // A plist boolean carries no text: <true/> or <false/>.
    private static string Boolean(XElement plist, string key) => Entry(plist.Element("dict")!, key)?.Name.LocalName ?? string.Empty;

    private static string? Text(XElement plist, params string[] keys) =>
        keys.Aggregate<string, XElement?>(plist.Element("dict"), (element, key) => element is null ? null : Entry(element, key))?.Value;

    private static string[] List(XElement plist, string key) =>
        Entry(plist.Element("dict")!, key)?.Elements("string").Select(element => element.Value).ToArray() ?? [];
}
