using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>Feature flags switching plan entries and desktops on and off, and the logon deferral.</summary>
public sealed class PlanFeaturesTests
{
    private static WorkspacePlan EntryPlan() => new()
    {
        Features = Features(("wt-terminals", true), ("warp-terminals", false)),
        Desktops =
        [
            new DesktopPlan("Work")
            {
                Launch =
                [
                    new LaunchEntry("WT 1") { Feature = "wt-terminals" },
                    new LaunchEntry("Warp 1") { Feature = "warp-terminals" },
                    new LaunchEntry("Fork"),
                    new LaunchEntry("Retired") { Feature = "wt-terminals", Disabled = true },
                ],
            },
        ],
    };

    private static WorkspacePlan DesktopsPlan() => new()
    {
        Features = Features(("personal-desktop", false)),
        Desktops =
        [
            new DesktopPlan("Work") { Launch = [new LaunchEntry("Fork")] },
            new DesktopPlan("Personal") { Feature = "personal-desktop", Launch = [new LaunchEntry("Chrome Personal")] },
            new DesktopPlan("Admin") { Launch = [new LaunchEntry("Slack")] },
        ],
    };

    [Fact]
    public void Defaults_disable_entries_behind_an_off_feature() =>
        Assert.Equal(["Warp 1", "Retired"], DisabledNames(PlanFeatures.Apply(EntryPlan())));

    [Fact]
    public void Enabling_revives_a_paused_feature_and_disabling_pauses_one()
    {
        var applied = PlanFeatures.Apply(EntryPlan(), enable: ["warp-terminals"], disable: ["wt-terminals"]);

        Assert.Equal(["WT 1", "Retired"], DisabledNames(applied));
        Assert.Equal([new("wt-terminals", false), new("warp-terminals", true)], applied.Features.ToList());
    }

    [Fact]
    public void Entries_without_a_feature_are_never_touched() =>
        Assert.DoesNotContain("Fork", DisabledNames(PlanFeatures.Apply(EntryPlan(), disable: ["wt-terminals", "warp-terminals"])));

    [Fact]
    public void An_entry_disabled_in_the_plan_stays_disabled_when_its_feature_is_on() =>
        Assert.Contains("Retired", DisabledNames(PlanFeatures.Apply(EntryPlan(), enable: ["wt-terminals"])));

    [Fact]
    public void Applying_flags_leaves_the_source_plan_unchanged()
    {
        var plan = EntryPlan();

        _ = PlanFeatures.Apply(plan, disable: ["wt-terminals"]);

        Assert.Equal(["Retired"], DisabledNames(plan));
    }

    [Fact]
    public void An_unknown_feature_name_is_rejected_with_the_declared_ones()
    {
        var error = Assert.Throws<UnknownFeatureException>(() => PlanFeatures.Resolve(EntryPlan(), enable: ["wrap-terminals"]));

        Assert.Equal("Unknown feature(s): wrap-terminals. Declared features: warp-terminals, wt-terminals", error.Message);
        Assert.EndsWith("<none declared>", Assert.Throws<UnknownFeatureException>(() => PlanFeatures.Resolve(new WorkspacePlan(), disable: ["x"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_feature_enabled_and_disabled_at_once_is_rejected() =>
        Assert.Contains(
            "both enabled and disabled",
            Assert.Throws<UnknownFeatureException>(() => PlanFeatures.Resolve(EntryPlan(), enable: ["wt-terminals"], disable: ["wt-terminals"])).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A_plan_without_features_is_returned_untouched()
    {
        var plan = new WorkspacePlan { Desktops = [new DesktopPlan("Work") { Launch = [new LaunchEntry("Fork")] }] };

        Assert.Same(plan, PlanFeatures.Apply(plan));
    }

    [Fact]
    public void A_desktop_behind_an_off_feature_is_dropped_not_emptied_and_its_name_kept_to_retire()
    {
        var applied = PlanFeatures.Apply(DesktopsPlan());

        Assert.Equal(["Work", "Admin"], applied.Desktops.Select(desktop => desktop.Name));
        Assert.Equal(["Personal"], applied.RetiredDesktopNames);
        Assert.False(applied.ToJson().ContainsKey("retired_desktop_names"));
    }

    [Fact]
    public void Enabling_a_desktop_feature_restores_it_in_place()
    {
        var applied = PlanFeatures.Apply(DesktopsPlan(), enable: ["personal-desktop"]);

        Assert.Equal(["Work", "Personal", "Admin"], applied.Desktops.Select(desktop => desktop.Name));
        Assert.Empty(applied.RetiredDesktopNames);
    }

    [Fact]
    public void A_desktop_disabled_in_the_plan_is_dropped_whatever_its_feature_says()
    {
        var plan = new WorkspacePlan
        {
            Features = Features(("personal-desktop", true)),
            Desktops = [new DesktopPlan("Work"), new DesktopPlan("Personal") { Feature = "personal-desktop", Disabled = true }],
        };

        Assert.Equal(["Work"], PlanFeatures.Apply(plan, enable: ["personal-desktop"]).Desktops.Select(desktop => desktop.Name));
    }

    [Fact]
    public void Features_from_a_plan_file_switch_entries_off()
    {
        var plan = PlanFiles.Parse(
            """{"features": {"wt-terminals": true, "warp-terminals": "off"}, "desktops": [{"name": "Work", "launch": [{"name": "WT 1", "feature": "wt-terminals"}, {"name": "Warp 1", "feature": "warp-terminals"}]}]}""");

        Assert.Equal(["Warp 1"], DisabledNames(PlanFeatures.Apply(plan)));
    }

    [Fact]
    public void Flags_layer_registry_default_then_plan_then_stored_override()
    {
        var plan = new WorkspacePlan { Features = Features(("pinned", false), ("plan-only", true)) };

        var flagged = PlanFeatures.WithFlags(plan, [new PlanFlag("pinned", true), new PlanFlag("fresh", true), new PlanFlag("overridden", false, Override: true), new PlanFlag("plan-only", false, Override: false)]);

        Assert.Equal(
            [new("pinned", false), new("fresh", true), new("overridden", true), new("plan-only", false)],
            flagged.Features.ToList());
    }

    [Fact]
    public void Deferred_logon_terminals_are_the_enabled_ones_and_deferral_drops_only_their_command()
    {
        var deferred = new LaunchEntry("Claude") { Type = LaunchEntry.Terminal, Command = "claude", CommandAtLogon = LaunchEntry.CommandAtLogonDeferred };
        var plan = new WorkspacePlan
        {
            Desktops =
            [
                new DesktopPlan("Work") { Launch = [deferred, new LaunchEntry("Shell") { Command = "pwsh" }, deferred with { Name = "Off", Disabled = true }] },
                new DesktopPlan("Hidden") { Disabled = true, Launch = [deferred with { Name = "Hidden" }] },
            ],
        };

        Assert.Equal(["Claude"], PlanFeatures.DeferredLogonTerminals(plan).Select(entry => entry.Name));
        var commands = PlanFeatures.DeferLogonCommands(plan).Desktops[0].Launch.Select(entry => entry.Command);
        Assert.Equal([null, "pwsh", null], commands);
    }

    private static OrderedDictionary<string, bool> Features(params (string Name, bool On)[] features)
    {
        var map = new OrderedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (name, on) in features)
        {
            map[name] = on;
        }

        return map;
    }

    private static List<string> DisabledNames(WorkspacePlan plan) =>
        [.. plan.Desktops.SelectMany(desktop => desktop.Launch).Where(entry => entry.Disabled).Select(entry => entry.Name)];
}
