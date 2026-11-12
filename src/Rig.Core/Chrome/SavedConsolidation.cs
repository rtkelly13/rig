using Rig.Core.Vault;

namespace Rig.Core.Chrome;

/// <summary>A consolidation plan, with the window whose bookmarks bar each profile's deletes go through.</summary>
/// <param name="Plan">The saved groups to delete.</param>
/// <param name="Fullest">The window showing the most saved groups, by profile.</param>
public sealed record SavedGroupsPlan(ConsolidationPlan Plan, IReadOnlyDictionary<string, long> Fullest);

/// <summary>Saved groups deleted, and what failed (null when nothing did).</summary>
/// <param name="Deleted">How many saved groups were deleted.</param>
/// <param name="Stopped">What failed; null when nothing did.</param>
public sealed record Consolidation(int Deleted, string? Stopped = null);

/// <summary>
/// Plans and carries out deleting closed duplicate saved tab groups, one chip at a time, and remembers which profiles
/// a run left to look at again. Ported from <c>desktop_manager.chrome.saved_consolidation</c>.
/// </summary>
/// <param name="actions">Reads and deletes saved-group chips.</param>
/// <param name="desktops">Where the user is, so they are put back.</param>
/// <param name="say">Writes a line for the user.</param>
public sealed class SavedConsolidation(IChromeSavedGroupActions actions, IChromeDesktops desktops, Action<string> say)
{
    /// <summary>The marker's file name, under the install root.</summary>
    public const string MarkerName = "saved-groups.pending";

    /// <summary>The consolidation plan over every profile's fullest bookmarks bar, with the window showing it.</summary>
    public SavedGroupsPlan Plan(IEnumerable<ChromeWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var bars = windows
            .Select(window => (window.ProfileKey, Bar: (Handle: window.Handle, Groups: actions.SavedGroups(window.Handle, window.ProfileKey))))
            .GroupBy(seen => seen.ProfileKey, seen => seen.Bar, StringComparer.Ordinal)
            .ToList();
        var fullest = bars.ToDictionary(
            profile => profile.Key,
            profile => profile.MaxBy(bar => bar.Groups.Count).Handle,
            StringComparer.Ordinal);
        var groups = bars.SelectMany(profile => SavedGroups.UnionOfBars(profile.Select(bar => bar.Groups)));
        return new SavedGroupsPlan(SavedGroups.PlanConsolidation(groups), fullest);
    }

    /// <summary>
    /// Deletes the plan's closed duplicates one chip at a time. A title that fails is left for the next run; the other
    /// titles still go. Puts the user back on the desktop they started on.
    /// </summary>
    public Consolidation Consolidate(SavedGroupsPlan planned)
    {
        ArgumentNullException.ThrowIfNull(planned);
        var start = desktops.Current();
        try
        {
            return Steps(planned);
        }
        finally
        {
            desktops.ReturnTo(start);
        }
    }

    /// <summary>
    /// The file listing the profiles an <c>organise --consolidate-saved</c> run left saved groups to look at again in:
    /// <c>%LOCALAPPDATA%\shared-utilities\chrome-tabs\saved-groups.pending</c>.
    /// </summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="home">The user's home, for when <c>LOCALAPPDATA</c> is unset.</param>
    public static string MarkerPath(Func<string, string?> environment, string home) =>
        Path.Join(ChromeInstall.Root(environment, home), MarkerName);

    /// <summary>The profiles the marker at <paramref name="marker"/> lists; none when it is missing.</summary>
    public static IReadOnlySet<string> PendingProfiles(string marker)
    {
        try
        {
            return PyText.SplitLines(File.ReadAllText(marker)).Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Writes <paramref name="profiles"/> to the marker, sorted, one a line; removes it when there are none.</summary>
    public static void SetPendingProfiles(string marker, IEnumerable<string> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var sorted = profiles.Distinct(StringComparer.Ordinal).Order(PyText.CodePointOrder).ToList();
        if (sorted.Count == 0)
        {
            File.Delete(marker);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, string.Concat(sorted.Select(profile => profile + "\n")));
    }

    private Consolidation Steps(SavedGroupsPlan planned)
    {
        var deleted = 0;
        List<string> failed = [];
        foreach (var delete in planned.Plan.Deletes)
        {
            for (var copy = 0; copy < delete.Count; copy++)
            {
                var result = actions.DeleteSavedGroup(planned.Fullest[delete.Profile], delete.Profile, delete.Title);
                if (!result.Deleted)
                {
                    failed.Add($"{PyText.Repr(delete.Title)} [{delete.Profile}]: {result.Detail}");
                    say($"  kept {delete.Title}: {result.Detail}");
                    break;
                }

                deleted++;
                var held = result.Tabs.Count > 0 ? $": {string.Join(", ", result.Tabs.Select(tab => TabPlanText.Clip(tab, 40)))}" : string.Empty;
                say($"  deleted {delete.Title} ({result.Tabs.Count} saved tabs{held})");
            }
        }

        return new Consolidation(deleted, failed.Count > 0 ? "deleting saved groups " + string.Join("; ", failed) : null);
    }
}
