namespace Rig.Core.CommandSurface;

/// <summary>An observed executable registration and its reviewed capability/platform ownership.</summary>
public sealed record CommandOwner(string Id, string Capability, string Owner, IReadOnlyList<string> Platforms, string? AliasTo = null);

/// <summary>A precise pre-existing overlap, allowed only until the losing implementation is removed.</summary>
public sealed record OwnershipOverlap(string Capability, string Platform, IReadOnlyList<string> Owners)
{
    /// <summary>Order-independent identity for the shrinking overlap ratchet.</summary>
    public string Key => $"{Capability}|{Platform}|{string.Join(',', Owners.Order(StringComparer.Ordinal))}";
}

/// <summary>The migration ledger: executable registrations, exact temporary overlaps and permanent tombstones.</summary>
public sealed record OwnershipLedger(IReadOnlyList<CommandOwner> Commands, IReadOnlyList<OwnershipOverlap> Baseline, IReadOnlyList<string> Retired, IReadOnlyList<string> RetiredFiles);

/// <summary>One implementation per capability/platform. Aliases reference the canonical registration, never another alias.</summary>
public static class CommandOwnership
{
    /// <summary>Reject unreviewed surfaces, independent duplicates, retired implementations and baseline expansion.</summary>
    public static string[] Check(IReadOnlyCollection<string> discovered, OwnershipLedger ledger, OwnershipLedger? previous = null)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(ledger);
        var errors = new List<string>();
        var ids = discovered.ToHashSet(StringComparer.Ordinal);
        var rows = ledger.Commands.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var id in ids.Where(id => !rows.ContainsKey(id)))
        {
            errors.Add($"Unregistered executable surface: {id}");
        }

        foreach (var pair in rows)
        {
            if (pair.Value.Length != 1)
            {
                errors.Add($"Repeated ledger registration: {pair.Key}");
            }

            if (!ids.Contains(pair.Key))
            {
                errors.Add($"Stale ledger registration: {pair.Key}");
            }
        }

        foreach (var row in ledger.Commands)
        {
            if (row.Platforms.Count == 0 || row.Platforms.Any(p => p is not ("linux" or "macos" or "windows")) || string.IsNullOrWhiteSpace(row.Capability) || string.IsNullOrWhiteSpace(row.Owner))
            {
                errors.Add($"Invalid ownership: {row.Id}");
            }

            if (row.AliasTo is { } target && (!rows.TryGetValue(target, out var targets) || targets.Length != 1 || targets[0].AliasTo is not null
                || targets[0].Owner != row.Owner || targets[0].Capability != row.Capability || row.Platforms.Except(targets[0].Platforms, StringComparer.Ordinal).Any()))
            {
                errors.Add($"Alias must dispatch to its canonical owner: {row.Id} -> {target}");
            }
        }

        var overlaps = ledger.Commands.Where(c => c.AliasTo is null).SelectMany(c => c.Platforms.Select(p => (Row: c, Platform: p)))
            .GroupBy(c => (c.Row.Capability, c.Platform))
            .Select(g => new OwnershipOverlap(g.Key.Capability, g.Key.Platform, g.Select(c => c.Row.Owner).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
            .Where(g => g.Owners.Count > 1).ToArray();
        var allowed = ledger.Baseline.Select(b => b.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var overlap in overlaps.Where(o => !allowed.Contains(o.Key)))
        {
            errors.Add($"Overlapping implementations: {overlap.Key}");
        }

        foreach (var stale in allowed.Except(overlaps.Select(o => o.Key), StringComparer.Ordinal))
        {
            errors.Add($"Remove resolved baseline overlap: {stale}");
        }

        foreach (var retired in ledger.Retired.Intersect(ids, StringComparer.Ordinal))
        {
            errors.Add($"Retired executable resurrected: {retired}");
        }

        if (previous is not null)
        {
            foreach (var added in allowed.Except(previous.Baseline.Select(b => b.Key), StringComparer.Ordinal))
            {
                errors.Add($"Overlap baseline may only shrink: {added}");
            }

            foreach (var removed in previous.Retired.Except(ledger.Retired, StringComparer.Ordinal).Concat(previous.RetiredFiles.Except(ledger.RetiredFiles, StringComparer.Ordinal)))
            {
                errors.Add($"Retirement tombstones are permanent: {removed}");
            }
        }

        return errors.ToArray();
    }
}
