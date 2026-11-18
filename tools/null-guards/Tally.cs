using System.Globalization;

namespace Rig.NullGuards;

internal sealed class Tally
{
    private readonly SortedDictionary<string, int> kept = new(StringComparer.Ordinal);

    internal int Removed { get; private set; }

    internal void Remove() => Removed++;

    internal void Keep(string reason) => kept[reason] = kept.GetValueOrDefault(reason) + 1;

    internal void Add(Tally other)
    {
        Removed += other.Removed;
        foreach (var (reason, count) in other.kept)
        {
            kept[reason] = kept.GetValueOrDefault(reason) + count;
        }
    }

    internal string Summary()
    {
        var keptTotal = kept.Values.Sum();
        var reasons = string.Join(", ", kept.Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} {pair.Value}")));
        return string.Create(CultureInfo.InvariantCulture, $"removed {Removed}, kept {keptTotal}") + (keptTotal > 0 ? $" ({reasons})" : string.Empty);
    }
}
