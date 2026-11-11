using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// Every plan file in <c>desktop-manager/</c> reads and writes back exactly as Python writes it. Each snapshot in
/// <c>Snapshots/</c> is Python's own output, <c>json.dumps(parse_plan_json(...).to_dict(), indent=2)</c> for a plan
/// and <c>DesktopPlan.from_dict(...).to_dict()</c> for a desktop file, so a diff here is a divergence from the Python
/// reader. Python's round trip is not the identity (it drops defaults and normalises spellings), which is why the
/// snapshots are its output rather than the files themselves.
/// </summary>
public sealed class PlanFileSnapshotTests
{
    private static readonly string Repo = Path.GetDirectoryName(RepoTemp.Root)!;

    /// <summary>The plan files the snapshots cover.</summary>
    public static TheoryData<string> Plans => ["sample-workspace", "workspace-plan.bundle"];

    /// <summary>The desktop files the bundle lists.</summary>
    public static TheoryData<string> Desktops => ["admin", "lab", "personal", "review", "work"];

    [Theory]
    [MemberData(nameof(Plans))]
    public void A_plan_file_writes_back_as_python_writes_it(string plan) =>
        Assert.Equal(Snapshot(plan), PlanFiles.Format(LoadPlan(plan).ToJson()));

    [Theory]
    [MemberData(nameof(Desktops))]
    public void A_desktop_file_writes_back_as_python_writes_it(string desktop)
    {
        var path = Path.Join(Repo, "desktop-manager", "bundles", desktop + ".json");

        Assert.Equal(Snapshot("bundles-" + desktop), PlanFiles.Format(PlanFiles.ParseDesktop(File.ReadAllText(path), path).ToJson()));
    }

    [Theory]
    [MemberData(nameof(Plans))]
    public void Writing_a_plan_and_reading_it_back_changes_nothing(string plan)
    {
        var once = PlanFiles.Format(LoadPlan(plan).ToJson());

        Assert.Equal(once, PlanFiles.Format(PlanFiles.Parse(once).ToJson()));
    }

    [Fact]
    public void A_saved_bundle_reads_back_as_the_same_plan()
    {
        using var temp = new TempDir();
        var plan = LoadPlan("workspace-plan.bundle");
        var root = Path.Join(temp.Path, "plan.json");

        PlanFiles.SaveBundle(plan, root);

        Assert.Equal(PlanFiles.Format(plan.ToJson()), PlanFiles.Format(PlanFiles.Load(root).ToJson()));
    }

    [Fact]
    public void A_plan_using_every_field_in_lax_spellings_writes_back_as_python_writes_it() =>
        Assert.Equal(Snapshot("full-plan"), PlanFiles.Format(FullPlan().ToJson()));

    [Fact]
    public void A_plan_using_every_field_survives_a_bundle_save()
    {
        using var temp = new TempDir();
        var plan = FullPlan();
        var root = Path.Join(temp.Path, "plan.json");

        PlanFiles.SaveBundle(plan, root);

        Assert.Equal(PlanFiles.Format(plan.ToJson()), PlanFiles.Format(PlanFiles.Load(root).ToJson()));
    }

    [Fact]
    public void Plan_errors_carry_their_message_and_cause()
    {
        var cause = new InvalidOperationException("cause");

        Assert.Same(cause, new InvalidPlanException("plan", cause).InnerException);
        Assert.Same(cause, new UnknownFeatureException("feature", cause).InnerException);
        Assert.NotEmpty(new InvalidPlanException().Message);
        Assert.NotEmpty(new UnknownFeatureException().Message);
    }

    private static WorkspacePlan FullPlan() =>
        PlanFiles.Parse(File.ReadAllText(Path.Join(Repo, "tests", "Rig.Core.Tests", "Desktop", "Snapshots", "full-plan.input.json")));

    private static WorkspacePlan LoadPlan(string name) => PlanFiles.Load(Path.Join(Repo, "desktop-manager", name + ".json"));

    // A checkout may turn the snapshot's line endings into CRLF; PlanFiles.Format always writes LF.
    private static string Snapshot(string name) =>
        File.ReadAllText(Path.Join(Repo, "tests", "Rig.Core.Tests", "Desktop", "Snapshots", name + ".json")).ReplaceLineEndings("\n");
}
