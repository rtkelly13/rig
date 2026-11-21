using Rig.Core.Jobs;

namespace Rig.Core.Tests.Jobs;

/// <summary>A rig run from an installed runtime copy keeps the copy's plan, .</summary>
public sealed class JobsSiteTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private string AppData => Path.Join(_temp.Path, "app");

    private string UserPlan => Path.Join(_temp.Path, "user", "windows-workspace.json");

    private string Personal => Path.Join(_temp.Path, "code", "personal");

    [Fact]
    public void A_development_task_publish_runs_as_its_own_checkout_only_when_the_project_exists()
    {
        var publish = Path.Join(_temp.Path, "temp", "jobs-runtime");
        Assert.Null(JobsSite.PublishedCheckout(publish));
        var project = _temp.File("src/Rig.Cli/Rig.Cli.csproj");
        File.WriteAllText(project, "<Project />");
        Assert.Equal(_temp.Path, JobsSite.PublishedCheckout(publish));
        Assert.Null(JobsSite.PublishedCheckout(Path.Join(_temp.Path, "elsewhere", "jobs-runtime")));
        Assert.Null(JobsSite.PublishedCheckout(_temp.Path));
    }

    [Theory]
    [InlineData("abc123def456")]
    [InlineData("current")]
    public void A_rig_published_in_a_runtime_copy_runs_as_that_copy_by_its_name_or_the_current_junction(string copy)
    {
        var folder = Path.Join(AppData, JobsSite.RuntimeFolder, copy);

        Assert.Equal(folder, JobsSite.CopyOf(Path.Join(folder, JobsSite.PublishFolder) + Path.DirectorySeparatorChar, AppData));
        Assert.Equal(folder, JobsSite.CopyOf(Path.Join(folder, JobsSite.PublishFolder), AppData));
        Assert.Equal(Path.Join(AppData, "runtime", "current", "rig", "rig"), JobsSite.StablePublishedRig(Path.Join(folder, JobsSite.PublishFolder), AppData, windows: false));
        Assert.Equal(Path.Join(AppData, "runtime", "current", "rig", "rig.exe"), JobsSite.StablePublishedRig(Path.Join(folder, JobsSite.PublishFolder), AppData, windows: true));
    }

    [Theory]
    [InlineData("code/personal/rig/src/Rig.Cli/bin/Debug/net10.0")]
    [InlineData("app/runtime/abc/rig/sub")]
    [InlineData("app/runtime/rig")]
    [InlineData("app/elsewhere/abc/rig")]
    [InlineData("app/runtime/abc/bin")]
    public void A_rig_anywhere_else_is_no_runtime_copy(string relative)
    {
        Assert.Null(JobsSite.CopyOf(Path.Join(_temp.Path, relative), AppData));
        Assert.Null(JobsSite.StablePublishedRig(Path.Join(_temp.Path, relative), AppData, windows: false));
    }

    [Fact]
    public void A_checkout_uses_the_per_user_plan_and_adds_nothing_to_the_jobs_environment()
    {
        var site = JobsSite.Locate(Path.Join(Personal, "rig"), AppData, UserPlan, Personal);

        Assert.Equal(UserPlan, site.Plan);
        Assert.Equal(Personal, site.PersonalRoot);
        Assert.Empty(site.Environment);
    }

    [Fact]
    public void A_runtime_copy_uses_its_bundled_plan_and_the_resolved_estate_even_when_installed_elsewhere()
    {
        var runtime = Path.Join(AppData, JobsSite.RuntimeFolder);
        Directory.CreateDirectory(runtime);
        var source = Path.Join(_temp.Path, "elsewhere", "personal", "rig");
        File.WriteAllText(Path.Join(runtime, "state.json"), $"{{\"current\": \"abc\", \"previous\": null, \"repo\": {System.Text.Json.JsonSerializer.Serialize(source)}}}");

        var site = JobsSite.Locate(Path.Join(runtime, "abc123def456"), AppData, UserPlan, Personal);

        Assert.Equal(Path.Join(runtime, "current", "desktop-manager", "workspace-plan.bundle.json"), site.Plan);
        Assert.Equal(Personal, site.PersonalRoot);
        Assert.Equal(Path.Join(AppData, "workspace-restore", "logs"), site.Environment[JobsSite.LogDirVariable]);
        Assert.Equal(Path.Join(AppData, "traces"), site.Environment[JobsSite.TraceHomeVariable]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("{\"repo\": \"\"}")]
    public void A_runtime_copy_without_a_recorded_source_keeps_the_given_personal_root(string? state)
    {
        var runtime = Path.Join(AppData, JobsSite.RuntimeFolder);
        Directory.CreateDirectory(runtime);
        if (state is not null)
        {
            File.WriteAllText(Path.Join(runtime, "state.json"), state);
        }

        var site = JobsSite.Locate(Path.Join(runtime, "abc123def456"), AppData, UserPlan, Personal);

        Assert.Equal(Personal, site.PersonalRoot);
        Assert.EndsWith("workspace-plan.bundle.json", site.Plan, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_beside_the_runtime_folder_is_not_a_copy()
    {
        Assert.Equal(UserPlan, JobsSite.Locate(Path.Join(AppData, "runtime-old", "abc"), AppData, UserPlan, Personal).Plan);
        Assert.Equal(UserPlan, JobsSite.Locate(Path.Join(AppData, JobsSite.RuntimeFolder), AppData, UserPlan, Personal).Plan);
    }
}
