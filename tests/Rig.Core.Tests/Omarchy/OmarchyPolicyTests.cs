using System.Text.Json.Nodes;
using Rig.Core.Omarchy;
using Rig.Core.Packages;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Omarchy;

/// <summary>The policy, the shell merge and the comparison behind <c>rig omarchy doctor</c>.</summary>
public sealed class OmarchyPolicyTests
{
    private static string CheckedInPolicy => Path.Join(Path.GetDirectoryName(RepoTemp.Root), OmarchyPolicy.RelativePath);

    [Fact]
    public void The_checked_in_policy_decodes()
    {
        var policy = OmarchyPolicy.Load(CheckedInPolicy);

        Assert.Equal(new IdlePolicy(0, 0), policy.Idle);
        Assert.Equal(new SuspendPolicy(false, "ignore", "ignore", "ignore", "ignore", "ignore"), policy.Suspend);
        Assert.True(policy.LockBeforeSuspend);
        Assert.Equal("tokyo-night", policy.Theme);
        Assert.Equal(new PowerPolicy("performance", "power-saver"), policy.Power);
        Assert.Equal(new FontPolicy(14), policy.Font);
        Assert.NotNull(policy.Shell);
        Assert.True(policy.Shell.ContainsKey("bar"));
        Assert.False(policy.Shell.ContainsKey("idle"));
    }

    [Fact]
    public void A_policy_leaves_out_the_sections_it_does_not_declare()
    {
        var policy = OmarchyPolicy.Parse("""{"theme": "tokyo-night"}""");

        Assert.Null(policy.Idle);
        Assert.Null(policy.Suspend);
        Assert.Null(policy.LockBeforeSuspend);
        Assert.Null(policy.Power);
        Assert.Null(policy.Font);
        Assert.Null(policy.Shell);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"idle": {"screensaver_seconds": 0}}""")]
    [InlineData("""{"idle": {"screensaver_seconds": -1, "lock_seconds": 0}}""")]
    [InlineData("""{"idle": {"screensaver_seconds": 86401, "lock_seconds": 0}}""")]
    [InlineData("""{"suspend": {"lid": "sleep"}}""")]
    [InlineData("""{"suspend": {"menu": "no"}}""")]
    [InlineData("""{"shell": {"idle": {"screensaver": 0}}}""")]
    [InlineData("""{"shell": []}""")]
    [InlineData("""{"theme": "  "}""")]
    [InlineData("""{"power": {"ac": "performance"}}""")]
    [InlineData("""{"font": {"base_size": 4}}""")]
    public void A_malformed_policy_is_refused(string text) =>
        Assert.Throws<InvalidDataException>(() => OmarchyPolicy.Parse(text));

    [Fact]
    public void The_shell_merge_keeps_unmanaged_keys_and_replaces_arrays()
    {
        var current = JsonNode.Parse("""
            {"version": 1, "bar": {"position": "top", "layout": {"left": [1]}}, "dock": {"pinned": ["foot"]}}
            """)!.AsObject();
        var desired = JsonNode.Parse("""
            {"bar": {"position": "bottom", "layout": {"left": [2, 3]}}, "plugins": ["x"]}
            """)!.AsObject();

        var merged = ShellDocument.Merge(current, desired);

        Assert.Equal(1, (int)merged["version"]!);
        Assert.Equal("bottom", (string?)merged["bar"]!["position"]);
        Assert.Equal(2, merged["bar"]!["layout"]!["left"]!.AsArray().Count);
        Assert.Equal("foot", (string?)merged["dock"]!["pinned"]![0]);
        Assert.Equal("x", (string?)merged["plugins"]![0]);
        Assert.Single(ShellDocument.Differences(current, desired), difference => difference.Path == "bar.position");
    }

    [Fact]
    public void Array_differences_are_reported_per_element()
    {
        var current = JsonNode.Parse("""{"right": [{"id": "a"}]}""")!.AsObject();
        var desired = JsonNode.Parse("""{"right": [{"id": "a"}, {"id": "b"}]}""")!.AsObject();

        var difference = Assert.Single(ShellDocument.Differences(current, desired));

        Assert.Equal(("right[1]", "(missing)"), (difference.Path, difference.Have));
        Assert.Contains("\"b\"", difference.Want, StringComparison.Ordinal);
    }

    [Fact]
    public void ALong_value_is_truncated_for_the_table()
    {
        var current = new JsonObject();
        var desired = new JsonObject { ["value"] = new string('x', 200) };

        var difference = Assert.Single(ShellDocument.Differences(current, desired));

        Assert.EndsWith("...", difference.Want, StringComparison.Ordinal);
        Assert.Equal(120, difference.Want.Length);
    }

    [Fact]
    public void AFullyDeclaredShell_has_no_differences()
    {
        var shell = JsonNode.Parse("""{"bar": {"position": "top"}}""")!.AsObject();

        Assert.Empty(ShellDocument.Differences(shell, (JsonObject)shell.DeepClone()));
    }

    [Fact]
    public void Compare_finds_every_drift()
    {
        var state = new OmarchyState(
            true,
            "/usr/share/omarchy",
            JsonNode.Parse("""{"idle": {"screensaver": 150, "lock": 300}, "bar": {"position": "top"}}""")!.AsObject(),
            "tokyo-night",
            "performance",
            null,
            false,
            14,
            null,
            true);
        var policy = OmarchyPolicy.Parse("""
            {"idle": {"screensaver_seconds": 0, "lock_seconds": 0},
             "suspend": {"menu": false, "lid": "ignore"},
             "lock_before_suspend": true,
             "theme": "tokyo-night",
             "power": {"ac": "performance", "battery": "power-saver"},
             "font": {"base_size": 14},
             "shell": {"bar": {"position": "top"}}}
            """);

        var checks = OmarchyPlan.Compare(policy, state);

        Assert.Equal(CheckStatus.Ok, checks.Single(check => check.Area == "theme").Status);
        Assert.DoesNotContain(checks, check => check.Area == "shell");
        Assert.Equal(CheckStatus.Ok, checks.Single(check => check.Setting == "lock-before-suspend").Status);
        Assert.Equal(CheckStatus.Ok, checks.Single(check => check.Area == "power" && check.Setting == "ac").Status);
        Assert.Equal(CheckStatus.Fail, checks.Single(check => check.Area == "power" && check.Setting == "battery").Status);
        Assert.Equal(CheckStatus.Fail, checks.Single(check => check.Area == "idle" && check.Setting == "screensaver").Status);
        Assert.Equal(CheckStatus.Fail, checks.Single(check => check.Area == "idle" && check.Setting == "lock").Status);
        Assert.Equal(CheckStatus.Fail, checks.Single(check => check.Area == "suspend").Status);
        Assert.All(checks.Where(check => check.Area == "logind" && check.Setting != "lock-before-suspend"), check => Assert.Equal(CheckStatus.Fail, check.Status));
        Assert.Equal("(unset)", checks.Single(check => check.Setting == "HandleLidSwitch").Have);
    }

    [Fact]
    public void ASuspendMenu_that_is_already_hidden_is_ok()
    {
        var policy = OmarchyPolicy.Parse("""{"suspend": {"menu": false}}""");
        var state = new OmarchyState(true, "/o", new JsonObject(), null, null, null, true, null, null, null);

        var check = Assert.Single(OmarchyPlan.Compare(policy, state), item => item.Area == "suspend");

        Assert.Equal(CheckStatus.Ok, check.Status);
    }

    [Fact]
    public void The_install_is_reported_first_and_alone_when_Omarchy_is_missing()
    {
        var policy = OmarchyPolicy.Parse("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");
        var state = new OmarchyState(false, "/usr/share/omarchy", new JsonObject(), null, null, null, false, null, null, null);

        var check = Assert.Single(OmarchyPlan.Compare(policy, state));

        Assert.Equal(("omarchy", "install", CheckStatus.Fail), (check.Area, check.Setting, check.Status));
    }

    [Fact]
    public void The_logind_body_names_only_the_declared_keys()
    {
        var suspend = new SuspendPolicy(Lid: "ignore", IdleAction: "ignore");

        Assert.Equal("HandleLidSwitch=ignore\nHandleLidSwitchExternalPower=ignore\nHandleLidSwitchDocked=ignore\nIdleAction=ignore\n", OmarchyPlan.LogindContents(suspend));
        Assert.Equal("[Login]\n" + OmarchyPlan.LogindContents(suspend), OmarchySync.LogindFile(suspend));
    }
}
