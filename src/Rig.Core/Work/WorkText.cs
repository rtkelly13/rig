using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rig.Core.Text;

namespace Rig.Core.WorkItems;

/// <summary>The work list as a person reads it: what each table cell says, without any styling.</summary>
public static partial class WorkText
{
    /// <summary>The table's column headings.</summary>
    public static IReadOnlyList<string> Columns { get; } = ["State", "Repo", "Branch", "Ticket", "PR", "Local", "Sessions", "Age", "Why"];

    /// <summary>The cell that stands for "nothing here".</summary>
    public const string None = "-";

    private const int SessionIdLength = 8;
    private const int SessionTitleLength = 70;

    /// <summary>One item's row, a cell per column.</summary>
    /// <param name="item">The work item.</param>
    /// <param name="now">The moment ages are measured from.</param>
    public static IReadOnlyList<string> Cells(WorkItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        return
        [
            item.State.Wire(),
            item.Repo is { Length: > 0 } repo ? repo : None,
            item.Branch is { Length: > 0 } branch ? branch : None,
            item.Ticket ?? string.Empty,
            Pr(item),
            Local(item),
            Sessions(item),
            Ago(item.LastActivity, now),
            string.Join("; ", item.Reasons),
        ];
    }

    /// <summary>The row listing one session under its item, a cell per column.</summary>
    /// <param name="session">The session.</param>
    /// <param name="now">The moment ages are measured from.</param>
    public static IReadOnlyList<string> SessionCells(SessionFacts session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        return
        [
            string.Empty,
            string.Empty,
            "  " + Clip(session.SessionId, SessionIdLength),
            string.Empty,
            string.Empty,
            string.Empty,
            Brief(session.State),
            Ago(session.LastActivity, now),
            Clip(session.Title, SessionTitleLength),
        ];
    }

    /// <summary>The line under the table: how many items are shown, by state, and how many the filters hid.</summary>
    /// <param name="total">How many items the report holds.</param>
    /// <param name="shown">The items that passed the filters.</param>
    public static string Summary(int total, IReadOnlyList<WorkItem> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var counts = WorkJoin.CountStates(shown);
        var states = string.Join("  ", WorkJoin.StateOrder.Where(state => counts[state] > 0).Select(state => Invariant($"{state.Wire()}={counts[state]}")));
        return Invariant($"{shown.Count} items ({states}); {total - shown.Count} hidden by filters");
    }

    /// <summary>How long ago <paramref name="moment"/> was: minutes under an hour, hours under two days, then days.</summary>
    /// <param name="moment">The moment, or null when there is none.</param>
    /// <param name="now">The moment it is measured from.</param>
    public static string Ago(DateTimeOffset? moment, DateTimeOffset now)
    {
        if (moment is not { } then)
        {
            return None;
        }

        var seconds = (now - then).TotalSeconds;
        if (seconds < 3600)
        {
            return Invariant($"{Math.Max((long)Math.Floor(seconds / 60), 0)}m");
        }

        return seconds < 86400 * 2
            ? Invariant($"{(long)Math.Floor(seconds / 3600)}h")
            : Invariant($"{(long)Math.Floor(seconds / 86400)}d");
    }

    /// <summary><c>7d</c>, <c>24h</c>, <c>2w</c>, or an ISO date or date and time (without an offset it is UTC).</summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="now">The moment a relative span counts back from.</param>
    /// <exception cref="FormatException"><paramref name="text"/> is neither.</exception>
    public static DateTimeOffset ParseSince(string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Relative().Match(text.Trim().ToLowerInvariant());
        if (match.Success && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            var hours = match.Groups[2].ValueSpan[0] switch
            {
                'h' => 1,
                'd' => 24,
                _ => 24 * 7,
            };
            return now - TimeSpan.FromHours((double)amount * hours);
        }

        return IsoTime.ParseIso(text) ?? throw new FormatException($"expected 7d / 24h / 2w or an ISO date, got {QuotedText.Str(text)}");
    }

    /// <summary>A session state in the few letters the table has room for.</summary>
    /// <param name="state">The session state.</param>
    public static string Brief(WorkSessionState state) => state switch
    {
        WorkSessionState.Live => "live",
        WorkSessionState.WaitingOnUser => "waiting",
        WorkSessionState.DiedMidTask => "died-mid",
        WorkSessionState.DiedIdle => "died-idle",
        WorkSessionState.EndedClean => "ended",
        _ => "cleared",
    };

    /// <summary>The first <paramref name="length"/> characters of <paramref name="text"/>, never splitting one.</summary>
    internal static string Clip(string text, int length)
    {
        var clipped = new StringBuilder();
        foreach (var rune in text.EnumerateRunes().Take(length))
        {
            _ = clipped.Append(rune);
        }

        return clipped.ToString();
    }

    private static string Pr(WorkItem item)
    {
        if (item.Pr is { } pr)
        {
            return Invariant($"#{pr.Number} {pr.Status.Wire()}");
        }

        return item.PrLookup switch
        {
            PrLookup.Unavailable => "?",
            PrLookup.Skipped => None,
            _ => "none",
        };
    }

    private static string Local(WorkItem item)
    {
        var parts = new List<string>();
        if (item.Dirty)
        {
            parts.Add("dirty");
        }

        if (item.Unpushed != 0)
        {
            parts.Add(Invariant($"{item.Unpushed} unpushed"));
        }

        if (item.BranchRef is { } reference)
        {
            if (reference.UpstreamGone)
            {
                parts.Add("gone");
            }
            else if (reference.Ahead is not (null or 0) || reference.Behind is not (null or 0))
            {
                parts.Add(Invariant($"+{reference.Ahead ?? 0}/-{reference.Behind ?? 0}"));
            }
        }

        if (item.Checkouts.Count > 1)
        {
            parts.Add(Invariant($"{item.Checkouts.Count} wt"));
        }

        return parts.Count > 0 ? string.Join(' ', parts) : None;
    }

    // Each state once, in the order the item's sessions first show it.
    private static string Sessions(WorkItem item) =>
        item.Sessions.Count == 0
            ? None
            : string.Join(' ', item.Sessions.GroupBy(session => session.State).Select(group => Invariant($"{group.Count()} {Brief(group.Key)}")));

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^([0-9]+)([dhw])$")]
    private static partial Regex Relative();
}
