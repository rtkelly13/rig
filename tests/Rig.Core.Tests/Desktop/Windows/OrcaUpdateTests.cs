using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;

namespace Rig.Core.Tests.Desktop.Windows;

/// <summary>
/// The platform-neutral half of the Orca update: reading versions and the staged installer, verifying it, and picking
/// the entries to relaunch. Replaces the matching cases of <c>tests/desktop_manager/test_orca_update.py</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OrcaUpdateTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Versions_compare_numerically_and_ignore_trailing_zero_parts()
    {
        Assert.Equal(0, OrcaUpdateState.VersionKey("1.4.250")!.CompareTo(OrcaUpdateState.VersionKey("1.4.250.0")!));
        Assert.True(OrcaUpdateState.VersionKey("1.4.9")!.CompareTo(OrcaUpdateState.VersionKey("1.4.10")!) < 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.x.2")]
    public void A_version_that_is_not_numeric_has_no_key(string? version) =>
        Assert.Null(OrcaUpdateState.VersionKey(version));

    [Theory]
    [InlineData("1.4.251.0", "1.4.250", true)]
    [InlineData("1.4.250.0", "1.4.250", false)]
    [InlineData("1.4.249", "1.4.250", false)]
    [InlineData("1.4.251", null, false)]
    public void An_update_is_pending_only_when_a_strictly_newer_build_is_staged(string staged, string? installed, bool pending)
    {
        var state = new OrcaUpdateState(installed, new(staged, "setup.exe", "x", false), false, false);

        Assert.Equal(pending, state.Pending);
    }

    [Fact]
    public void Nothing_staged_is_never_pending()
    {
        var state = new OrcaUpdateState("1.4.250", null, true, false);

        Assert.False(state.Pending);
        Assert.Null(state.StagedVersion);
        Assert.Contains("staged=none", state.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_enabled_entries_that_start_orca_are_relaunched_in_plan_order()
    {
        var plan = new WorkspacePlan
        {
            Desktops =
            [
                new("Work")
                {
                    Launch =
                    [
                        new("Work Orca") { Path = @"C:\x\Orca.exe" },
                        new("Terminal") { Path = "wt.exe", WindowProcessName = "WindowsTerminal.exe" },
                        new("Off Orca") { Path = @"C:\x\Orca.exe", Disabled = true },
                        new("Personal Orca") { Path = "{orca-path}", WindowProcessName = "Orca.exe" },
                    ],
                },
            ],
        };

        var names = OrcaUpdate.Entries(plan).Select(item => item.Entry.Name);

        Assert.Equal(["Work Orca", "Personal Orca"], names);
    }

    [Fact]
    public void A_staged_installer_is_read_from_the_pending_folder()
    {
        var pending = Pending();
        File.WriteAllText(Path.Combine(pending, "orca-windows-setup.exe"), "x");
        File.WriteAllText(
            Path.Combine(pending, "update-info.json"),
            """{"fileName": "orca-windows-setup.exe", "sha512": "abc", "isAdminRightsRequired": true}""");

        var staged = OrcaUpdate.Staged(_temp.Path);

        Assert.NotNull(staged);
        Assert.Equal(Path.Combine(pending, "orca-windows-setup.exe"), staged.Installer);
        Assert.Equal("abc", staged.Sha512);
        Assert.True(staged.AdminRequired);
    }

    [Fact]
    public void A_missing_installer_means_nothing_is_staged()
    {
        File.WriteAllText(Path.Combine(Pending(), "update-info.json"), """{"fileName": "orca-windows-setup.exe"}""");

        Assert.Null(OrcaUpdate.Staged(_temp.Path));
    }

    [Fact]
    public void The_file_name_cannot_escape_the_pending_folder()
    {
        var pending = Pending();
        File.WriteAllText(Path.Combine(_temp.Path, "evil.exe"), "x");
        File.WriteAllText(Path.Combine(pending, "update-info.json"), """{"fileName": "../../evil.exe"}""");

        Assert.Null(OrcaUpdate.Staged(_temp.Path));
    }

    [Fact]
    public void The_installer_must_match_its_recorded_sha512()
    {
        var installer = Path.Combine(_temp.Path, "setup.exe");
        File.WriteAllText(installer, "installer bytes");
        var good = Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes("installer bytes")));

        Assert.True(OrcaUpdate.MatchesSha512(installer, good));
        Assert.False(OrcaUpdate.MatchesSha512(installer, Convert.ToBase64String(SHA512.HashData("other"u8))));
        Assert.False(OrcaUpdate.MatchesSha512(installer, null));
        Assert.False(OrcaUpdate.MatchesSha512(installer, "not base64!"));
    }

    [Fact]
    public void An_unverified_or_admin_installer_is_never_run()
    {
        var installer = Path.Combine(_temp.Path, "setup.exe");
        File.WriteAllText(installer, "x");
        var messages = new List<string>();

        Assert.False(OrcaUpdate.RunInstaller(new("1.4.251", installer, "AAAA", AdminRequired: false), messages.Add));
        Assert.False(OrcaUpdate.RunInstaller(new("1.4.251", installer, "AAAA", AdminRequired: true), messages.Add));
        Assert.Contains(messages, message => message.Contains("SHA-512", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("admin", StringComparison.Ordinal));
    }

    private string Pending() => _temp.Folder(Path.Combine("orca-updater", "pending"));
}
