using Rig.Core.Processes;
using Rig.Core.Services;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Services;

/// <summary><see cref="Launchd"/> against a fake <c>launchctl</c>; nothing here loads a real agent.</summary>
public sealed class LaunchdTests : IDisposable
{
    private readonly TempDir temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => temp.Dispose();

    [Fact]
    public void The_gui_domain_and_agents_folder_are_the_users()
    {
        Assert.Equal("gui/501", Launchd.GuiDomain(501));
        Assert.Equal(Path.Join("/home/u", "Library", "LaunchAgents"), Launchd.AgentsDirUnder("/home/u"));
        Assert.Equal("/agents", new Launchd(new FakeProc(), "/agents", "gui/0").AgentsDir);
    }

    [Fact]
    public async Task Loading_boots_out_the_old_copy_before_writing_the_new_one()
    {
        var path = Path.Join(temp.Path, "Agents", "a.plist");
        var proc = new Probe(path);
        await File.WriteAllTextAsync(Path.Join(temp.Folder("Agents"), "a.plist"), "old", Token);

        await new Launchd(proc, temp.Path, "gui/501").LoadAsync(path, "new"u8.ToArray(), Token);

        Assert.Equal([("launchctl bootout gui/501 " + path, "old"), ("launchctl bootstrap gui/501 " + path, "new")], proc.Seen);
    }

    [Fact]
    public async Task Loading_creates_the_agents_folder_and_a_failed_bootstrap_throws()
    {
        var path = Path.Join(temp.Path, "new", "a.plist");
        var proc = new FakeProc().Answer(new Ran(5, string.Empty, "Bootstrap failed"), "launchctl", "bootstrap");

        var error = await Assert.ThrowsAsync<ProcException>(() => new Launchd(proc, temp.Path, "gui/501").LoadAsync(path, [1], Token));

        Assert.Equal([1], await File.ReadAllBytesAsync(path, Token));
        Assert.Contains("bootstrap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unloading_ignores_an_agent_that_was_not_loaded()
    {
        var proc = new FakeProc { Unanswered = new Ran(3, string.Empty, "No such process") };

        await new Launchd(proc, temp.Path, "gui/501").UnloadAsync("/a.plist", Token);

        Assert.Equal(["launchctl", "bootout", "gui/501", "/a.plist"], Assert.Single(proc.Requests).Argv);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(113, false)]
    public async Task Loaded_is_whether_launchctl_print_succeeds(int code, bool loaded)
    {
        var proc = new FakeProc().Answer(new Ran(code, string.Empty, string.Empty), "launchctl", "print", "gui/501/a.b");

        Assert.Equal(loaded, await new Launchd(proc, temp.Path, "gui/501").LoadedAsync("a.b", Token));
    }

    [Fact]
    public async Task Kickstart_kills_first_only_when_restarting_and_throws_on_failure()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        var launchd = new Launchd(proc, temp.Path, "gui/501");

        await launchd.KickstartAsync("a.b", cancellationToken: Token);
        await launchd.KickstartAsync("a.b", restart: true, Token);
        proc.Answer(new Ran(1, string.Empty, string.Empty), "launchctl");

        await Assert.ThrowsAsync<ProcException>(() => launchd.KickstartAsync("a.b", cancellationToken: Token));
        Assert.Equal(
            [["launchctl", "kickstart", "gui/501/a.b"], ["launchctl", "kickstart", "-k", "gui/501/a.b"], ["launchctl", "kickstart", "gui/501/a.b"]],
            proc.Requests.Select(r => r.Argv));
    }

    // Records each command with the plist's text at the moment it ran, so the order of writes and launchctl calls shows.
    private sealed class Probe(string path) : IProc
    {
        public List<(string Command, string Plist)> Seen { get; } = [];

        public async Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default)
        {
            Seen.Add((string.Join(' ', request.Argv), await File.ReadAllTextAsync(path, cancellationToken)));
            return new Ran(0, string.Empty, string.Empty);
        }
    }
}
