using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Rig.Core.Chrome.Extension;

namespace Rig.Core.Tests.Chrome.Extension;

/// <summary>The extension shipped in <c>extensions/chrome-tabs</c> agrees with the names rig uses for it.</summary>
public sealed class ExtensionManifestTests
{
    private static readonly string Folder = Path.Combine(RepoTemp.Root, "..", "extensions", "chrome-tabs");

    private static JsonObject Manifest() => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "manifest.json")))!.AsObject();

    [Fact]
    public void The_manifest_key_fixes_the_extension_id()
    {
        var key = Convert.FromBase64String((string)Manifest()["key"]!);

        // Chrome's id: the first 16 bytes of the key's SHA-256, each nibble written as a letter from a to p.
        var id = string.Concat(SHA256.HashData(key)[..16].SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 0xf)) }));

        Assert.Equal(ExtensionProtocol.ExtensionId, id);
    }

    [Fact]
    public void The_background_script_connects_to_the_host_rig_registers()
    {
        var script = File.ReadAllText(Path.Combine(Folder, (string)Manifest()["background"]!["service_worker"]!));

        Assert.Contains($"\"{ExtensionProtocol.HostName}\"", script, StringComparison.Ordinal);
        foreach (var command in new[] { ExtensionProtocol.WindowsCommand, ExtensionProtocol.CloseCommand, ExtensionProtocol.ActivateCommand, ExtensionProtocol.GroupCommand, ExtensionProtocol.UngroupCommand, ExtensionProtocol.MoveCommand })
        {
            Assert.Contains($"{command}:", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_extension_asks_only_for_what_the_driver_uses()
    {
        Assert.Equal(
            ["identity", "identity.email", "nativeMessaging", "tabGroups", "tabs"],
            Manifest()["permissions"]!.AsArray().Select(permission => (string)permission!).Order(StringComparer.Ordinal));
        Assert.Null(Manifest()["host_permissions"]);
    }
}
