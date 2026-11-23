using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Chrome;
using Rig.Core.Chrome.Extension;
using Rig.Core.Tests;

namespace Rig.Cli.Tests.Chrome;

public sealed class ChromeExtensionCommandTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly StringWriter _out = new() { NewLine = "\n" };

    public void Dispose()
    {
        _home.Dispose();
        _out.Dispose();
    }

    [Fact]
    public void Chrome_starting_rig_as_a_host_runs_the_host()
    {
        Assert.Equal(["chrome-tabs", "extension", "host"], ChromeExtensionCommand.HostArguments([ExtensionProtocol.Origin]));
        Assert.Equal(["chrome-tabs", "extension", "host"], ChromeExtensionCommand.HostArguments([ExtensionProtocol.Origin, "--parent-window=0"]));
        Assert.Equal(["chrome-tabs", "list"], ChromeExtensionCommand.HostArguments(["chrome-tabs", "list"]));
        Assert.Empty(ChromeExtensionCommand.HostArguments([]));
    }

    [Fact]
    public void Install_then_status_names_each_piece_and_the_connected_profiles()
    {
        var source = _home.Folder("shipped");
        File.WriteAllText(Path.Combine(source, "manifest.json"), "{}");
        _home.Folder(".config/chromium");
        var command = new ChromeExtensionCommand(
            new ExtensionInstaller(_home.Path, "/usr/local/bin/rig"),
            new OneProfile(),
            source,
            _home.Folder("sockets"),
            _out).Build();

        Assert.Equal(0, command.Parse(["install"]).Invoke());
        Assert.Equal(0, command.Parse(["status"]).Invoke());

        var text = _out.ToString();
        Assert.Contains("Host manifest (chromium): ok", text, StringComparison.Ordinal);
        Assert.Contains("Chromium flags: loads the extension", text, StringComparison.Ordinal);
        Assert.Contains("Connected: chromium me@example.com, extension 0.1.0, 5.sock", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_fails_when_no_profile_is_connected()
    {
        var command = new ChromeExtensionCommand(new ExtensionInstaller(_home.Path, "rig"), new NoProfiles(), _home.Path, _home.Path, _out).Build();

        Assert.Equal(1, command.Parse(["status"]).Invoke());
        Assert.Contains("Connected: none", _out.ToString(), StringComparison.Ordinal);
    }

    private sealed class OneProfile : IExtensionBridge
    {
        public IReadOnlyList<ExtensionConnection> Connections() => [new("/run/rig/chrome-tabs/5.sock", "me@example.com", "0.1.0", "/usr/lib/chromium/chromium")];

        public JsonNode? Send(ExtensionConnection connection, string command, JsonObject? args = null) => null;
    }

    private sealed class NoProfiles : IExtensionBridge
    {
        public IReadOnlyList<ExtensionConnection> Connections() => [];

        public JsonNode? Send(ExtensionConnection connection, string command, JsonObject? args = null) => throw new ExtensionCallException("none");
    }
}
