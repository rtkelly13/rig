using System.Text.Json.Nodes;
using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>devenv's git identities and includes, and Windows Terminal's settings. Ported from <c>test_steps.py</c>, <c>test_terminal.py</c> and <c>test_config.py</c>.</summary>
public sealed class GitTerminalSetupTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    private static readonly GitIdentity Personal = new("personal", "Dev", "dev@example.com", null, "~/code/personal/");

    [Fact]
    public void Identities_come_from_the_devenv_config_and_skip_incomplete_entries()
    {
        Assert.Empty(GitSetup.Identities(_home.Path));
        File.WriteAllText(_home.File(".config/devenv/config.json"), """
            {"custom_catalogs": ["~/x.json"], "identities": [
              {"id": "work", "name": "W", "email": "w@example.com", "signing_key": "ABC", "match_directory": "~/code/work"},
              {"id": "broken", "name": "B"}, "not-an-object"]}
            """);

        Assert.Equal([new GitIdentity("work", "W", "w@example.com", "ABC", "~/code/work")], GitSetup.Identities(_home.Path));
    }

    [Fact]
    public void Adding_an_identity_keeps_the_rest_of_the_config()
    {
        var config = _home.File(".config/devenv/config.json");
        File.WriteAllText(config, """{"custom_catalogs": ["~/x.json"]}""");

        Assert.True(GitSetup.AddIdentity(_home.Path, Personal, windows: false));
        Assert.True(GitSetup.AddIdentity(_home.Path, Personal with { Id = "second", SigningKey = "K" }, windows: false));

        var saved = JsonNode.Parse(File.ReadAllText(config))!;
        Assert.Equal("~/x.json", saved["custom_catalogs"]![0]!.GetValue<string>());
        Assert.Equal([Personal, Personal with { Id = "second", SigningKey = "K" }], GitSetup.Identities(_home.Path));

        File.WriteAllText(config, "[1]");
        Assert.False(GitSetup.AddIdentity(_home.Path, Personal, windows: false));
        Assert.Equal("[1]", File.ReadAllText(config));
    }

    [Fact]
    public void A_missing_config_is_created_when_an_identity_is_added()
    {
        Assert.True(GitSetup.AddIdentity(_home.Path, Personal, windows: false));

        Assert.Equal([Personal], GitSetup.Identities(_home.Path));
    }

    [Fact]
    public void Each_routed_identity_gets_a_profile_and_one_include_block()
    {
        Assert.Equal("[user]\n\tname = Dev\n\temail = dev@example.com\n", GitSetup.Profile(Personal));
        Assert.EndsWith("\tsigningkey = K\n", GitSetup.Profile(Personal with { SigningKey = "K" }), StringComparison.Ordinal);

        var profile = Path.Join("/h", ".gitconfig-personal");
        var once = GitSetup.WithIncludes("[core]\n\teditor = vim", "/h", [Personal, Personal with { Id = "loose", MatchDirectory = null }]);

        Assert.Equal($"[core]\n\teditor = vim\n\n# Managed identity: Dev (personal)\n[includeIf \"gitdir:~/code/personal/\"]\n\tpath = {profile}\n", once);
        Assert.Equal(once, GitSetup.WithIncludes(once, "/h", [Personal with { MatchDirectory = "~/code/personal\\" }]));
    }

    [Fact]
    public void The_ssh_key_is_ed25519_then_rsa()
    {
        Assert.Null(GitSetup.SshKey(_home.Path));
        var rsa = _home.File(".ssh/id_rsa");
        Assert.Equal(rsa, GitSetup.SshKey(_home.Path));
        var ed = _home.File(".ssh/id_ed25519");
        Assert.Equal(ed, GitSetup.SshKey(_home.Path));
    }

    [Fact]
    public void The_defaults_set_autocrlf_per_platform()
    {
        Assert.Contains(("core.autocrlf", "true"), GitSetup.Defaults(windows: true));
        Assert.Contains(("core.autocrlf", "input"), GitSetup.Defaults(windows: false));
        Assert.Contains(("init.defaultBranch", "main"), GitSetup.Defaults(windows: false));
    }

    [Fact]
    public void The_terminal_settings_are_the_stable_then_preview_then_unpackaged_file()
    {
        var local = _home.Path;
        Assert.Null(TerminalSetup.SettingsPath(local));
        var unpackaged = Path.Join(_home.Folder("Microsoft/Windows Terminal"), "settings.json");
        Assert.Equal(unpackaged, TerminalSetup.SettingsPath(local));
        var preview = _home.File("Packages/Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe/LocalState/settings.json");
        Assert.Equal(preview, TerminalSetup.SettingsPath(local));
        var stable = Path.Join(_home.Folder("Packages/Microsoft.WindowsTerminal_8wekyb3d8bbwe/LocalState"), "settings.json");
        Assert.Equal(preview, TerminalSetup.SettingsPath(local));
        File.WriteAllText(stable, "{}");
        Assert.Equal(stable, TerminalSetup.SettingsPath(local));
    }

    [Fact]
    public void Status_reads_snap_to_grid_through_comments_and_trailing_commas()
    {
        Assert.Equal(new TerminalStatus(null, null), TerminalSetup.Status(null));
        var path = _home.File("settings.json");
        File.WriteAllText(path, "// managed\n{\n  \"snapToGridOnResize\": false, // off\n}\n");
        Assert.Equal(new TerminalStatus(path, false), TerminalSetup.Status(path));
        File.WriteAllText(path, "not json");
        Assert.Equal(new TerminalStatus(path, null), TerminalSetup.Status(path));
        Assert.Null(TerminalSetup.Load(path));
    }

    [Fact]
    public void Apply_sets_each_default_once()
    {
        var settings = new JsonObject { ["profiles"] = new JsonObject { ["list"] = new JsonArray(), ["defaults"] = new JsonObject { ["font"] = new JsonObject { ["size"] = 12 } } } };

        Assert.Equal(
            ["Disabled snapToGridOnResize (prevents snap break and margin jitter)", "Set font face to F", "Set starting directory to C:\\code"],
            TerminalSetup.Apply(settings, "F", "C:\\code"));
        Assert.Empty(TerminalSetup.Apply(settings, "F", "C:\\code"));
        Assert.Equal(12, settings["profiles"]!["defaults"]!["font"]!["size"]!.GetValue<int>());
        Assert.NotNull(settings["profiles"]!["list"]);

        var bare = new JsonObject { ["profiles"] = "odd" };
        Assert.Contains("Set font size to 10", TerminalSetup.Apply(bare, "F", "/c"));
        Assert.Equal(10, bare["profiles"]!["defaults"]!["font"]!["size"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("/h/.zshrc", false, "eval \"$(oh-my-posh init zsh)\"")]
    [InlineData("/h/.bashrc", false, "eval \"$(oh-my-posh init bash)\"")]
    [InlineData("C:\\h\\Microsoft.PowerShell_profile.ps1", true, "oh-my-posh init pwsh | Invoke-Expression")]
    public void The_prompt_hook_matches_the_profiles_shell(string profile, bool windows, string hook) =>
        Assert.Equal(hook, TerminalSetup.PromptHook(profile, windows));
}
