using System.Text.Json.Nodes;
using Rig.Core.Roots;
using Rig.Core.Services;
using Rig.Core.Tests.Fakes;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// Holds <see cref="KnowledgeServer"/> and <see cref="AppData"/> to <c>rig.obsidian.knowledge_server</c> and
/// <c>rig.roots.app_data_dir</c>. <c>knowledge-server-oracle.json</c> records what the Python made of <c>~/.claude.json</c>
/// and <c>package.json</c> texts (lenient pydantic decodes), the <c>claude mcp</c> commands it would run, its LaunchAgent
/// plists byte for byte, and its app-data folder for macOS and Linux. Top-level <c>NaN</c> and <c>Infinity</c>, which
/// pydantic's parser accepted, are a deliberate difference; <see cref="KnowledgeServerTests"/> holds it.
/// </summary>
public sealed class KnowledgeServerOracleTests
{
    private static readonly JsonObject Oracle =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vault", "knowledge-server-oracle.json")))!.AsObject();

    [Fact]
    public void Constants_match_the_python()
    {
        var constants = Oracle["constants"]!;
        Assert.Equal(KnowledgeServer.Label, constants["label"]!.GetValue<string>());
        Assert.Equal(KnowledgeServer.Package, constants["package"]!.GetValue<string>());
        Assert.Equal(KnowledgeServer.Version, constants["version"]!.GetValue<string>());
        Assert.Equal(KnowledgeServer.Port, constants["port"]!.GetValue<int>());
        Assert.Equal(KnowledgeServer.ServerName, constants["server_name"]!.GetValue<string>());
        Assert.Equal(constants["mcp_url"]!.GetValue<string>(), KnowledgeServer.McpUrl());
        Assert.Equal(constants["editor_url"]!.GetValue<string>(), KnowledgeServer.EditorUrl());
        Assert.Equal(KnowledgeServer.ThrottleSeconds, constants["throttle"]!.GetValue<int>());
    }

    [Fact]
    public void Claude_json_reads_and_connects_as_python_did()
    {
        var mismatches = new List<string>();
        foreach (var node in Oracle["registrations"]!.AsArray())
        {
            var text = Convert.FromBase64String(node!["b64"]!.GetValue<string>());
            if (text.AsSpan().IndexOf("NaN"u8) >= 0 || text.AsSpan().IndexOf("Infinity"u8) >= 0)
            {
                continue;
            }

            var read = KnowledgeServer.ReadRegistration(text);
            var expected = new Registration(node["transport"]?.GetValue<string>(), node["target"]?.GetValue<string>());
            var connect = new JsonArray([.. KnowledgeServer.ConnectCommands(read, "/bin/claude").Select(c => new JsonArray([.. c.Select(a => JsonValue.Create(a))]))]);
            if (read != expected || read.Shared != node["shared"]!.GetValue<bool>() || !JsonNode.DeepEquals(connect, node["connect"]))
            {
                mismatches.Add($"{Convert.ToBase64String(text)}: {read} {connect.ToJsonString()}, Python {expected} {node["connect"]!.ToJsonString()}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join('\n', mismatches));
    }

    [Fact]
    public void Package_manifests_read_as_python_did()
    {
        using var temp = new TempDir();
        foreach (var node in Oracle["manifests"]!.AsArray())
        {
            var manifest = temp.File(Path.Join("node_modules", KnowledgeServer.Package, "package.json"));
            File.WriteAllBytes(manifest, Convert.FromBase64String(node!["b64"]!.GetValue<string>()));

            Assert.Equal(node["version"]?.GetValue<string>(), KnowledgeServer.InstalledVersion(temp.Path));
        }
    }

    [Fact]
    public void Launch_agents_render_byte_for_byte()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The recorded plists hold POSIX paths.");
        using var handler = new HttpClientHandler();
        using var http = new HttpMessageInvoker(handler);
        foreach (var node in Oracle["plists"]!.AsArray().Select(n => n!.AsObject()))
        {
            var places = new KnowledgeServerPlaces("/unused", AppData.Resolve(node["local_app_data"]!.GetValue<string>(), null, "/unused", macOs: true));
            var server = new KnowledgeServer(new FakeProc(), new Launchd(new FakeProc(), "/unused", "gui/0"), places, _ => null, http);
            var (node_, script, root, port) = (node["node"]!.GetValue<string>(), node["script"]!.GetValue<string>(), node["root"]!.GetValue<string>(), node["port"]!.GetValue<int>());

            Assert.Equal(node["log"]!.GetValue<string>(), places.LogPath);
            Assert.Equal(node["args"]!.AsArray().Select(a => a!.GetValue<string>()), KnowledgeServer.ProgramArguments(node_, script, root, port));
            Assert.Equal(Convert.FromBase64String(node["b64"]!.GetValue<string>()), server.Render(node_, script, root, port));
        }
    }

    [Fact]
    public void The_app_data_folder_is_where_python_put_it()
    {
        foreach (var node in Oracle["app_data"]!.AsArray().Select(n => n!.AsObject()))
        {
            var (local, xdg, home) = (node["local_app_data"]?.GetValue<string>(), node["xdg_data_home"]?.GetValue<string>(), node["home"]!.GetValue<string>());
            var label = $"LOCALAPPDATA={local ?? "unset"} XDG_DATA_HOME={xdg ?? "unset"}";

            Assert.Equal((label, node["mac"]!.GetValue<string>()), (label, Posix(AppData.Resolve(local, xdg, home, macOs: true))));
            Assert.Equal((label, node["linux"]!.GetValue<string>()), (label, Posix(AppData.Resolve(local, xdg, home, macOs: false))));
        }
    }

    // The recorded folders are POSIX paths; on Windows, Path.Join puts a backslash between the parts it adds.
    private static string Posix(string path) => path.Replace('\\', '/');
}
