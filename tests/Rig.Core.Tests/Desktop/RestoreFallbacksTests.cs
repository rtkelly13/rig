using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// Grouping launches that failed on a missing path. Replaces the missing-path half of
/// <c>tests/desktop_manager/test_restore_pure_helpers.py</c>.
/// </summary>
public sealed class RestoreFallbacksTests
{
    [Theory]
    [InlineData(@"Launch entry `MyApp` resolved path does not exist: C:\missing\app.exe", true)]
    [InlineData(@"Launch entry `Term` resolved path does not exist: C:\missing\cwd", true)]
    [InlineData("Something went wrong", false)]
    [InlineData(@"resolved path does not exist: C:\foo.exe", false)]
    [InlineData("Launch entry `MyApp` failed to start", false)]
    public void A_missing_path_error_names_both_the_entry_and_the_path(string message, bool missing) =>
        Assert.Equal(missing, RestoreFailureFallbacks.IsMissingPathError(message));

    [Theory]
    [InlineData(@"Launch entry `MyApp` resolved path does not exist: C:\missing\app.exe", @"Launch target resolved path does not exist: C:\missing\app.exe")]
    [InlineData(@"Launch entry `Terminal` resolved cwd does not exist: C:\missing\dir", @"Launch target resolved cwd does not exist: C:\missing\dir")]
    [InlineData(@"Launch entry `Editor` resolved arg does not exist: C:\missing\file.txt", @"Launch target resolved arg does not exist: C:\missing\file.txt")]
    [InlineData("Launch entry `E` resolved arg does not exist: x\n", "Launch target resolved arg does not exist: x")]
    [InlineData("Some other error message that does not match the pattern", "Some other error message that does not match the pattern")]
    public void A_missing_path_message_loses_its_entry_name(string message, string normalized) =>
        Assert.Equal(normalized, RestoreFailureFallbacks.NormalizeMissingPathErrorMessage(message));

    [Fact]
    public void Entries_missing_the_same_path_group_under_the_first_desktop_once_each()
    {
        var fallbacks = new RestoreFailureFallbacks();
        LaunchEntry a = new("A"), b = new("B"), c = new("C");
        Assert.False(fallbacks.HasMissingPaths);

        fallbacks.RecordMissingPath(1, a, "Launch entry `A` resolved path does not exist: X");
        fallbacks.RecordMissingPath(1, b, "Launch entry `B` resolved path does not exist: X");
        fallbacks.RecordMissingPath(1, a, "Launch entry `A` resolved path does not exist: X");
        fallbacks.RecordMissingPath(2, c, "Launch entry `C` resolved path does not exist: X");
        fallbacks.RecordMissingPath(2, c, "Launch entry `C` resolved cwd does not exist: Y");

        Assert.True(fallbacks.HasMissingPaths);
        var groups = fallbacks.MissingPathEntries;
        Assert.Equal(["Launch target resolved path does not exist: X", "Launch target resolved cwd does not exist: Y"], groups.Select(group => group.Key));
        Assert.Equal(1, groups[0].Value.DesktopIndex);
        Assert.Equal(["A", "B"], groups[0].Value.Entries.Select(entry => entry.Name));
        Assert.Equal(2, groups[1].Value.DesktopIndex);
        Assert.Equal(["C"], groups[1].Value.Entries.Select(entry => entry.Name));
    }
}
