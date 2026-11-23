using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Core.Processes;

/// <summary>How a branch tracks its upstream, as <c>git for-each-ref</c>'s <c>%(upstream:track)</c> reports it.</summary>
/// <param name="Ahead">Commits ahead; null when there is no upstream or it is gone.</param>
/// <param name="Behind">Commits behind; null when there is no upstream or it is gone.</param>
/// <param name="Gone">The tracked remote branch no longer exists.</param>
public sealed partial record Track(int? Ahead, int? Behind, bool Gone)
{
    /// <summary>
    /// <c>[ahead 2, behind 1]</c>, <c>[gone]</c> or empty. Without an upstream, ahead and behind are unknown, not zero.
    /// </summary>
    /// <param name="upstream">The branch's upstream, empty when it has none.</param>
    /// <param name="track">The <c>%(upstream:track)</c> text.</param>
    public static Track Parse(string upstream, string track)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(track);
        if (upstream.Length == 0)
        {
            return new Track(null, null, false);
        }

        if (track.Trim() == "[gone]")
        {
            return new Track(null, null, true);
        }

        var parts = TrackPart().Matches(track);
        int Count(string side) =>
            parts.Where(match => match.Groups[1].Value == side)
                .Select(match => int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
                .LastOrDefault();

        return new Track(Count("ahead"), Count("behind"), false);
    }

    [GeneratedRegex(@"(ahead|behind) (\d+)")]
    private static partial Regex TrackPart();
}
