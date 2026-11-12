using System.Text.RegularExpressions;
using Rig.Core.Vault;

namespace Rig.Core.Chrome;

/// <summary>One saved-group chip as one window's bookmarks bar shows it, left to right by <paramref name="Position"/>.</summary>
/// <param name="Profile">The profile whose bar shows it.</param>
/// <param name="Title">The group's title.</param>
/// <param name="Opened">The group is open in some window.</param>
/// <param name="Position">Its place on the bar, 0 leftmost.</param>
public sealed record SavedGroup(string Profile, string Title, bool Opened, int Position);

/// <summary>Delete <paramref name="Count"/> of the <paramref name="ClosedCopies"/> closed saved copies titled <paramref name="Title"/>.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="Title">The title.</param>
/// <param name="Count">How many to delete.</param>
/// <param name="OpenCopies">Open copies, which stay.</param>
/// <param name="ClosedCopies">Closed copies.</param>
public sealed record SavedGroupDelete(string Profile, string Title, int Count, int OpenCopies, int ClosedCopies);

/// <summary>What a saved-group chip's accessible name says.</summary>
/// <param name="Title">The group's title.</param>
/// <param name="Opened">Whether it is open.</param>
public sealed record ChipName(string Title, bool Opened);

/// <summary>A title saved more than once with <paramref name="Count"/> copies open.</summary>
/// <param name="Profile">The profile.</param>
/// <param name="Title">The title.</param>
/// <param name="Count">Open copies.</param>
public sealed record OpenDuplicate(string Profile, string Title, int Count);

/// <summary>The saved copies to delete, the open duplicates only reported, and every group seen.</summary>
/// <param name="Deletes">The deletes, one per title.</param>
/// <param name="OpenDuplicates">Titles with more than one open copy: Chrome must have them open, so only reported.</param>
/// <param name="Groups">Every group seen, by profile then position.</param>
public sealed record ConsolidationPlan(
    IReadOnlyList<SavedGroupDelete> Deletes,
    IReadOnlyList<OpenDuplicate> OpenDuplicates,
    IReadOnlyList<SavedGroup> Groups)
{
    /// <summary>How many saved groups the plan deletes.</summary>
    public int Total => Deletes.Sum(delete => delete.Count);
}

/// <summary>
/// Chrome's saved tab groups, read off the bookmarks bar, and the plan that deletes the duplicates: for each title with
/// an open copy every closed copy, for a title with only closed copies all but the leftmost. Open copies are never
/// touched. Ported from <c>desktop_manager.chrome.saved_groups</c>.
/// </summary>
public static partial class SavedGroups
{
    /// <summary><c>" Google group – Opened"</c> is <c>ChipName("Google", true)</c>; null for anything else on the bar.</summary>
    public static ChipName? ParseChipName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var match = ChipPattern().Match(name);
        return match.Success ? new ChipName(match.Groups["title"].Value, match.Groups["state"].Value == "Opened") : null;
    }

    /// <summary>
    /// One profile's saved groups from the bars of its windows: every window shows the same chips in the same order, as
    /// many as fit, so the bar showing the most is the fullest list.
    /// </summary>
    public static IReadOnlyList<SavedGroup> UnionOfBars(IEnumerable<IReadOnlyList<SavedGroup>> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IReadOnlyList<SavedGroup> widest = [];
        foreach (var bar in bars)
        {
            if (bar.Count > widest.Count)
            {
                widest = bar;
            }
        }

        return widest;
    }

    /// <summary>What to delete so each title keeps one saved copy: the open one, else the leftmost closed one.</summary>
    public static ConsolidationPlan PlanConsolidation(IEnumerable<SavedGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var ordered = groups.OrderBy(group => group.Profile, PyText.CodePointOrder).ThenBy(group => group.Position).ToList();
        var deletes = new List<SavedGroupDelete>();
        var openDuplicates = new List<OpenDuplicate>();
        foreach (var copies in ordered.GroupBy(group => (group.Profile, group.Title)))
        {
            var opened = copies.Count(copy => copy.Opened);
            var closed = copies.Count() - opened;
            if (opened > 1)
            {
                openDuplicates.Add(new(copies.Key.Profile, copies.Key.Title, opened));
            }

            var surplus = opened > 0 ? closed : closed - 1;
            if (surplus > 0)
            {
                deletes.Add(new(copies.Key.Profile, copies.Key.Title, surplus, opened, closed));
            }
        }

        return new ConsolidationPlan(deletes, openDuplicates, ordered);
    }

    [GeneratedRegex(@"^\s*(?<title>.*?) group – (?<state>Opened|Closed)\s*$")]
    private static partial Regex ChipPattern();
}
