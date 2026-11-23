using System.Text.Json.Nodes;
using Rig.Core.Chrome.Extension;

namespace Rig.Core.Tests.Chrome.Extension;

public sealed class ExtensionInstallerTests : IDisposable
{
    private const string Omarchy = "--ozone-platform=wayland\n--load-extension=/usr/share/a,/usr/share/b\n--no-default-browser-check\n";
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void The_folder_joins_an_existing_load_extension_list_once()
    {
        var once = ExtensionInstaller.WithExtension(Omarchy, "/x");

        Assert.Equal("--ozone-platform=wayland\n--load-extension=/usr/share/a,/usr/share/b,/x\n--no-default-browser-check\n", once);
        Assert.Equal(once, ExtensionInstaller.WithExtension(once, "/x"));
    }

    [Fact]
    public void Without_a_list_the_flag_is_added_and_removed_whole()
    {
        var added = ExtensionInstaller.WithExtension("--a\n", "/x");

        Assert.Equal("--a\n--load-extension=/x\n", added);
        Assert.Equal("--a\n", ExtensionInstaller.WithoutExtension(added, "/x"));
        Assert.Equal("--load-extension=/x\n", ExtensionInstaller.WithExtension("", "/x"));
        Assert.Equal("", ExtensionInstaller.WithoutExtension("--load-extension=/x\n", "/x"));
    }

    [Fact]
    public void Removing_leaves_the_other_extensions()
    {
        var installed = ExtensionInstaller.WithExtension(Omarchy, "/x");

        Assert.Equal(Omarchy, ExtensionInstaller.WithoutExtension(installed, "/x"));
        Assert.Equal(Omarchy, ExtensionInstaller.WithoutExtension(Omarchy, "/x"));
    }

    [Fact]
    public void Install_copies_registers_and_flags_then_uninstall_reverses_it()
    {
        var source = Source();
        _home.Folder(".config/chromium");
        File.WriteAllText(Path.Combine(_home.Path, ".config", "chromium-flags.conf"), Omarchy);
        var installer = new ExtensionInstaller(_home.Path, "/home/me/.local/bin/rig");

        installer.Install(source);

        Assert.True(File.Exists(Path.Combine(installer.ExtensionFolder, "manifest.json")));
        var manifest = JsonNode.Parse(File.ReadAllText(installer.HostManifest("chromium")))!;
        Assert.Equal(ExtensionProtocol.HostName, (string)manifest["name"]!);
        Assert.Equal("/home/me/.local/bin/rig", (string)manifest["path"]!);
        Assert.Equal([ExtensionProtocol.Origin], manifest["allowed_origins"]!.AsArray().Select(origin => (string)origin!));
        Assert.Contains(installer.ExtensionFolder, File.ReadAllText(installer.FlagsFile), StringComparison.Ordinal);
        Assert.False(File.Exists(installer.HostManifest("google-chrome")));
        Assert.All(installer.Check().Lines, line => Assert.DoesNotContain("missing", line, StringComparison.Ordinal));

        Assert.All(installer.Install(source).Lines, line => Assert.StartsWith("Unchanged", line, StringComparison.Ordinal));

        installer.Uninstall();

        Assert.Equal(Omarchy, File.ReadAllText(installer.FlagsFile));
        Assert.False(File.Exists(installer.HostManifest("chromium")));
        Assert.False(Directory.Exists(installer.ExtensionFolder));
    }

    [Fact]
    public void Reinstalling_drops_files_the_new_copy_no_longer_has()
    {
        var source = Source();
        var installer = new ExtensionInstaller(_home.Path, "rig");
        installer.Install(source);
        File.WriteAllText(Path.Combine(installer.ExtensionFolder, "old.js"), "");

        installer.Install(source);

        Assert.False(File.Exists(Path.Combine(installer.ExtensionFolder, "old.js")));
    }

    [Fact]
    public void Installing_without_the_shipped_extension_fails_loudly() =>
        Assert.Throws<DirectoryNotFoundException>(() => new ExtensionInstaller(_home.Path, "rig").Install(_home.Folder("empty")));

    private string Source()
    {
        var source = _home.Folder("shipped");
        File.WriteAllText(Path.Combine(source, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(source, "background.js"), "");
        return source;
    }
}
