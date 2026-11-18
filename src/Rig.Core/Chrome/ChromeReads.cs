using System.Diagnostics;

namespace Rig.Core.Chrome;

/// <summary>
/// Reading Chrome's windows for a command: one <c>chrome_tabs.read_windows</c> span per read, carrying the phase it is
/// for.
/// </summary>
public static class ChromeReads
{
    private static readonly ActivitySource Source = new("Rig.Chrome");

    /// <summary>
    /// <paramref name="read"/>'s windows for <paramref name="profile"/>, as one span tagged with
    /// <paramref name="phase"/> and its window and tab counts.
    /// </summary>
    /// <param name="read">Reads every window.</param>
    /// <param name="profile">Only this profile, by directory or display name ignoring case; all of them for null.</param>
    /// <param name="phase">Which pass this read is for: <c>initial</c>, <c>regroup</c>, <c>wait_check</c>, <c>replan</c>, <c>retry</c>, <c>consolidate</c>.</param>
    public static IReadOnlyList<ChromeWindow> Read(Func<IReadOnlyList<ChromeWindow>> read, string? profile, string phase)
    {
        using var activity = Source.StartActivity("chrome_tabs.read_windows");
        activity?.SetTag("read.phase", phase);
        var windows = FilterProfile(read(), profile);
        activity?.SetTag("chrome.windows", windows.Count);
        activity?.SetTag("chrome.tabs", windows.Sum(window => window.Tabs.Count));
        return windows;
    }

    /// <summary>The windows of <paramref name="profile"/>, by directory or display name ignoring case; all of them for null.</summary>
    public static IReadOnlyList<ChromeWindow> FilterProfile(IReadOnlyList<ChromeWindow> windows, string? profile)
    {
        if (profile is null)
        {
            return windows;
        }

        return
        [
            .. windows.Where(window =>
                string.Equals(window.ProfileDir, profile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(window.ProfileName, profile, StringComparison.OrdinalIgnoreCase)),
        ];
    }
}
