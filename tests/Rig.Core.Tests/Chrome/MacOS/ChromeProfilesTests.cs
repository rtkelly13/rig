using Rig.Core.Chrome.MacOS;

namespace Rig.Core.Tests.Chrome.MacOS;

/// <summary>Chrome's profiles from a <c>Local State</c> file written to a temp home folder.</summary>
public sealed class ChromeProfilesTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("rig-chrome-profiles-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private void Write(string text)
    {
        var path = Path.Combine(_home, ChromeProfiles.LocalState);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Profiles_are_read_by_directory()
    {
        Write("""
            {"profile": {"info_cache": {
                "Default": {"name": "Home", "gaia_name": "Ryan K", "gaia_given_name": "Ryan", "user_name": "user@example.test", "avatar": 3},
                "Profile 4": {"name": "Work"}
            }}, "browser": {}}
            """);
        var profiles = ChromeProfiles.Read(_home);
        Assert.Equal(["Default", "Profile 4"], profiles.Keys);
        Assert.Equal(new ProfileInfo("Home", "Ryan K", "Ryan", "user@example.test"), profiles["Default"]);
        Assert.Equal(new ProfileInfo("Work"), profiles["Profile 4"]);
    }

    [Fact]
    public void A_missing_file_has_no_profiles() => Assert.Empty(ChromeProfiles.Read(_home));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"profile\": null}")]
    [InlineData("{\"profile\": []}")]
    [InlineData("{\"profile\": {}}")]
    [InlineData("{\"profile\": {\"info_cache\": []}}")]
    [InlineData("{\"profile\": {\"info_cache\": null}}")]
    [InlineData("{\"profile\": {\"info_cache\": {\"\\ud83d\": {}}}}")]
    public void A_file_not_in_chromes_shape_has_no_profiles(string text)
    {
        Write(text);
        Assert.Empty(ChromeProfiles.Read(_home));
    }

    [Fact]
    public void Entries_that_are_not_objects_are_skipped_and_odd_names_read_as_absent()
    {
        Write("""{"profile": {"info_cache": {"Default": "broken", "Profile 1": null, "Profile 2": {"name": 7, "user_name": "a@example.test"}}}}""");
        var profile = Assert.Single(ChromeProfiles.Read(_home));
        Assert.Equal("Profile 2", profile.Key);
        Assert.Equal(new ProfileInfo(UserName: "a@example.test"), profile.Value);
    }

    [Fact]
    public void A_name_with_a_lone_surrogate_reads_as_python_reads_it()
    {
        Write("""{"profile": {"info_cache": {"Default": {"name": "a\ud83db"}}}}""");
        Assert.Equal("a\ud83db", ChromeProfiles.Read(_home)["Default"].Name);
    }

    [Fact]
    public void Aliases_are_the_names_that_are_set()
    {
        Assert.Equal(new HashSet<string> { "Work", "Ryan" }, new ProfileInfo("Work", string.Empty, "Ryan", null).Aliases());
        Assert.Empty(new ProfileInfo().Aliases());
    }
}
