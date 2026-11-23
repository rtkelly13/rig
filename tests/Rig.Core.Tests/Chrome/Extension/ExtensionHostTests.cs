using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Rig.Core.Chrome.Extension;

namespace Rig.Core.Tests.Chrome.Extension;

/// <summary>The host relays between commands on its socket and the extension on Chrome's pipe.</summary>
public sealed class ExtensionHostTests : IDisposable
{
    // Unix socket paths are limited to about 100 bytes, so the folder is short rather than under TempDir.
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rigx-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Relays_requests_to_the_extension_and_answers_hello_itself()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The host runs on Linux; Windows uses UI Automation.");
        var cancel = TestContext.Current.CancellationToken;
        var toHost = new Pipe();
        var fromHost = new Pipe();
        var socket = ExtensionPlaces.SocketFor(_folder, 42);
        using var host = new ExtensionHost(toHost.Reader.AsStream(), fromHost.Writer.AsStream(), socket, TimeSpan.FromSeconds(5), "/usr/lib/chromium/chromium");
        var running = host.RunAsync(cancel);
        var chrome = toHost.Writer.AsStream();
        await NativeFrames.WriteAsync(chrome, new JsonObject { ["type"] = "hello", ["email"] = "me@example.com", ["version"] = "0.1.0" }, cancel);
        var bridge = new SocketExtensionBridge(_folder, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => File.Exists(socket), cancel);

        // The extension answers the one request it will get.
        async Task AnswerAsync()
        {
            var request = (await NativeFrames.ReadAsync(fromHost.Reader.AsStream(), cancel))!;
            Assert.Equal("close", (string)request["cmd"]!);
            Assert.Equal(9, (int)request["args"]!["tabId"]!);
            await NativeFrames.WriteAsync(chrome, new JsonObject { ["type"] = "response", ["id"] = request["id"]!.DeepClone(), ["ok"] = false, ["error"] = "No tab with id: 9" }, cancel);
        }

        var extension = AnswerAsync();

        var connection = await WaitForConnectionAsync(bridge, cancel);
        Assert.Equal(("me@example.com", "0.1.0", "chromium"), (connection.Email, connection.Version, connection.ConfigFolder));
        var refused = await Assert.ThrowsAsync<ExtensionCallException>(() => OnThread(() => bridge.Send(connection, "close", new JsonObject { ["tabId"] = 9 })));
        Assert.Contains("No tab with id: 9", refused.Message, StringComparison.Ordinal);
        await extension;

        await chrome.DisposeAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10), cancel);
        Assert.False(File.Exists(socket));
    }

    [Fact]
    public void A_socket_nothing_listens_on_is_removed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The host runs on Linux; Windows uses UI Automation.");
        Directory.CreateDirectory(_folder);
        var stale = ExtensionPlaces.SocketFor(_folder, 7);
        using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
        {
            socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(stale));
        }

        Assert.Empty(new SocketExtensionBridge(_folder, TimeSpan.FromSeconds(1)).Connections());
        Assert.False(File.Exists(stale));
    }

    [Theory]
    [InlineData("/run/user/1000", "/run/user/1000/rig/chrome-tabs")]
    [InlineData(null, "/home/me/.local/state/rig/chrome-tabs")]
    public void Sockets_live_in_the_runtime_folder_when_there_is_one(string? runtime, string expected) =>
        Assert.Equal(expected, ExtensionPlaces.SocketFolder(name => name == "XDG_RUNTIME_DIR" ? runtime : null, "/home/me").Replace('\\', '/'));

    private static async Task WaitForAsync(Func<bool> ready, CancellationToken cancel)
    {
        for (var tries = 0; !ready(); tries++)
        {
            Assert.True(tries < 200, "timed out");
            await Task.Delay(25, cancel);
        }
    }

    private static async Task<ExtensionConnection> WaitForConnectionAsync(SocketExtensionBridge bridge, CancellationToken cancel)
    {
        IReadOnlyList<ExtensionConnection> found = [];
        await WaitForAsync(() => (found = bridge.Connections()).Count > 0 && found[0].Email.Length > 0, cancel);
        return found[0];
    }

    /// <summary>Runs a blocking bridge call on a thread of its own, so the host's reads go on meanwhile; a refusal faults the task.</summary>
    private static Task<T> OnThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (ExtensionCallException e)
            {
                done.SetException(e);
            }
        })
        { IsBackground = true };
        thread.Start();
        return done.Task;
    }
}
