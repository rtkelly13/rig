using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>The Nerd Fonts: where they go, which are present, and installing one from a served zip.</summary>
public sealed class NerdFontsTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly FontServer _server = new();

    public void Dispose()
    {
        _server.Dispose();
        _home.Dispose();
    }

    private static NerdFont JetBrains => NerdFonts.All[0];

    private string Folder => Path.Join(_home.Path, "fonts");

    [Fact]
    public void The_folder_is_the_user_font_folder_for_each_system()
    {
        Assert.Equal(Path.Join("/h", "AppData", "Local", "Microsoft", "Windows", "Fonts"), NerdFonts.Folder("/h", windows: true, macOS: false));
        Assert.Equal(Path.Join("/h", "Library", "Fonts"), NerdFonts.Folder("/h", windows: false, macOS: true));
        Assert.Equal(Path.Join("/h", ".local", "share", "fonts"), NerdFonts.Folder("/h", windows: false, macOS: false));
    }

    [Fact]
    public void A_font_is_installed_when_a_ttf_or_otf_carries_its_name()
    {
        Assert.False(NerdFonts.Installed(Folder, JetBrains));
        _ = _home.File("fonts/JetBrainsMono.txt");
        Assert.False(NerdFonts.Installed(Folder, JetBrains));
        _ = _home.File("fonts/JetBrains Mono NerdFont-Bold.otf");
        Assert.True(NerdFonts.Installed(Folder, JetBrains));
        Assert.False(NerdFonts.Installed(Folder, NerdFonts.All[1]));
    }

    [Fact]
    public void The_release_url_names_the_zip() =>
        Assert.Equal("https://github.com/ryanoasis/nerd-fonts/releases/latest/download/FiraCode.zip", NerdFonts.Url(NerdFonts.All[2]).ToString());

    [Fact]
    public async Task Install_copies_the_font_files_flat_and_skips_windows_compatible_ones()
    {
        _ = _server.With(JetBrains, "JetBrainsMonoNerdFont-Regular.ttf", "nested/JetBrainsMonoNerdFontMono-Bold.otf", "Windows Compatible/JetBrainsMonoNerdFont-Wide.ttf", "README.md", "../escape.ttf");

        Assert.Null(await _server.Fonts.InstallAsync(JetBrains, Folder, TestContext.Current.CancellationToken));

        Assert.Equal(
            ["JetBrainsMonoNerdFont-Regular.ttf", "JetBrainsMonoNerdFontMono-Bold.otf", "escape.ttf"],
            Directory.EnumerateFiles(Folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Join(_home.Path, "escape.ttf")));
        Assert.True(NerdFonts.Installed(Folder, JetBrains));
    }

    [Fact]
    public async Task A_failed_download_or_bad_zip_is_reported_not_thrown()
    {
        Assert.Contains("404", await _server.Fonts.InstallAsync(JetBrains, Folder, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        _server.Zips[NerdFonts.Url(JetBrains).ToString()] = [1, 2, 3];
        Assert.NotNull(await _server.Fonts.InstallAsync(JetBrains, Folder, TestContext.Current.CancellationToken));
        Assert.False(NerdFonts.Installed(Folder, JetBrains));
    }
}
