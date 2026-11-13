using Rig.Core.CommandSurface;

namespace Rig.Core.Tests.CommandSurface;

public sealed class CommandOwnershipTests
{
    private static CommandOwner Owner(string id, string owner, string[]? platforms = null, string? alias = null) =>
        new(id, "chrome-tabs traces runs", owner, platforms ?? ["macos", "windows"], alias);

    private static OwnershipLedger Ledger(params CommandOwner[] commands) => new(commands, [], [], []);

    [Fact]
    public void A_new_unregistered_cli_is_refused() =>
        Assert.Contains(CommandOwnership.Check(["new executable"], Ledger()), e => e.Contains("Unregistered executable", StringComparison.Ordinal));

    [Fact]
    public void Two_independent_implementations_for_the_same_platform_are_refused()
    {
        var ledger = Ledger(Owner("rig", "C#"), Owner("legacy", "Python"));
        var errors = CommandOwnership.Check(["rig", "legacy"], ledger);
        Assert.Equal(2, errors.Length);
        Assert.All(errors, e => Assert.StartsWith("Overlapping implementations:", e, StringComparison.Ordinal));
    }

    [Fact]
    public void A_thin_alias_to_one_canonical_implementation_passes()
    {
        var ledger = Ledger(Owner("rig", "C#"), Owner("legacy", "C#", alias: "rig"));
        Assert.Empty(CommandOwnership.Check(["rig", "legacy"], ledger));
    }

    [Fact]
    public void Disjoint_partial_ports_pass()
    {
        var ledger = Ledger(Owner("rig", "C#", ["windows"]), Owner("legacy", "Python", ["macos"]));
        Assert.Empty(CommandOwnership.Check(["rig", "legacy"], ledger));
    }

    [Fact]
    public void Independent_subcommands_can_have_different_owners()
    {
        var ledger = Ledger(Owner("runs", "C#"), Owner("install", "Python") with { Capability = "chrome-tabs install" });
        Assert.Empty(CommandOwnership.Check(["runs", "install"], ledger));
    }

    [Fact]
    public void Retired_implementation_cannot_be_reregistered_even_with_an_ownership_row()
    {
        var ledger = Ledger(Owner("legacy", "Python")) with { Retired = ["legacy"] };
        Assert.Equal(["Retired executable resurrected: legacy"], CommandOwnership.Check(["legacy"], ledger));
    }

    [Fact]
    public void The_baseline_cannot_expand_or_hide_a_third_implementation()
    {
        var old = Ledger(Owner("rig", "C#", ["macos"]), Owner("legacy", "Python", ["macos"])) with
        {
            Baseline = [new("chrome-tabs traces runs", "macos", ["C#", "Python"])],
        };
        Assert.Empty(CommandOwnership.Check(["rig", "legacy"], old, old));
        var expanded = old with
        {
            Commands = [.. old.Commands, Owner("other", "Other", ["macos"])],
            Baseline = [new("chrome-tabs traces runs", "macos", ["C#", "Python", "Other"])],
        };
        Assert.Contains(CommandOwnership.Check(["rig", "legacy", "other"], expanded, old), e => e.StartsWith("Overlap baseline may only shrink", StringComparison.Ordinal));
        Assert.Contains(CommandOwnership.Check(["rig", "legacy", "other"], expanded with { Baseline = old.Baseline }, old), e => e.StartsWith("Overlapping implementations", StringComparison.Ordinal));
    }

    [Fact]
    public void Removed_duplicates_must_leave_the_baseline_and_tombstones_stay()
    {
        var previous = Ledger(Owner("rig", "C#")) with { Retired = ["old"], RetiredFiles = ["old.py"] };
        var next = previous with { Baseline = [new("resolved", "windows", ["one", "two"])], Retired = [], RetiredFiles = [] };
        var errors = CommandOwnership.Check(["rig"], next, previous);
        Assert.Contains(errors, e => e.StartsWith("Remove resolved baseline overlap", StringComparison.Ordinal));
        Assert.Contains("Retirement tombstones are permanent: old", errors);
        Assert.Contains("Retirement tombstones are permanent: old.py", errors);
    }

    [Theory]
    [InlineData("missing", "C#", "macos")]
    [InlineData("rig", "Python", "macos")]
    [InlineData("rig", "C#", "linux")]
    [InlineData("alias", "C#", "macos")]
    public void Unresolvable_mismatched_and_chained_aliases_are_refused(string target, string owner, string platform)
    {
        var ledger = Ledger(Owner("rig", "C#", ["macos"]), Owner("alias", "C#", ["macos"], "rig"), Owner("bad", owner, [platform], target));
        Assert.Contains(CommandOwnership.Check(["rig", "alias", "bad"], ledger), e => e.StartsWith("Alias must dispatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_stale_and_repeated_rows_are_refused()
    {
        var ledger = Ledger(Owner("one", "C#", []), Owner("one", "C#"), Owner("stale", " ", ["plan9"]) with { Capability = "" });
        var errors = CommandOwnership.Check(["one"], ledger);
        Assert.Contains("Repeated ledger registration: one", errors);
        Assert.Contains("Stale ledger registration: stale", errors);
        Assert.Equal(2, errors.Count(e => e.StartsWith("Invalid ownership", StringComparison.Ordinal)));
    }

    [Fact]
    public void Null_inputs_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => CommandOwnership.Check(null!, Ledger()));
        Assert.Throws<ArgumentNullException>(() => CommandOwnership.Check([], null!));
    }
}
