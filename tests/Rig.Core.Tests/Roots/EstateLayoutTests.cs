using Rig.Core.Roots;

namespace Rig.Core.Tests.Roots;

public sealed class EstateLayoutTests
{
    [Fact]
    public void Rig_home_is_both_checkouts_and_implies_the_root()
    {
        using var home = new TempDir();
        var rig = home.Folder("Work/personal/rig");

        var layout = EstateLayout.Resolve(Inputs(home.Path, new() { [RigCheckout.HomeVariable] = rig }));

        Assert.Equal((Path.Join(Path.GetFullPath(home.Path), "Work"), rig, rig), (layout.CodeRoot, layout.Checkout, layout.Source));
        Assert.Equal(Path.Join(layout.CodeRoot, "personal"), layout.Personal);
        Assert.Equal(Path.Join(rig, "config", EstateScopes.FileName), layout.ScopesFile);
    }

    [Fact]
    public void The_code_root_variable_wins_and_the_checkout_follows_it()
    {
        using var home = new TempDir();

        var layout = EstateLayout.Resolve(Inputs(home.Path, new() { [CodeRoot.EnvironmentVariable] = "~/src" }));

        Assert.Equal(Path.Combine(home.Path, "src"), layout.CodeRoot);
        Assert.Equal(Path.Combine(home.Path, "src", "personal", "rig"), layout.Checkout);
    }

    [Fact]
    public void A_build_output_implies_the_root_its_checkout_sits_in()
    {
        using var home = new TempDir();
        home.File("Work/personal/rig/rig.slnx");
        home.File("Work/personal/rig/src/Rig.Cli/Rig.Cli.csproj");
        var bin = home.Folder("Work/personal/rig/src/Rig.Cli/bin/Debug/net11.0");

        var layout = EstateLayout.Resolve(Inputs(home.Path, [], bin));

        Assert.Equal(Path.Join(Path.GetFullPath(home.Path), "Work"), layout.CodeRoot);
        Assert.Equal(Path.Join(Path.GetFullPath(home.Path), "Work", "personal", "rig"), layout.Source);
    }

    [Fact]
    public void An_installed_copy_implies_the_root_of_the_repo_it_recorded()
    {
        using var home = new TempDir();
        var repo = home.Folder("Work/personal/rig");
        var appData = home.Folder("appdata");
        File.WriteAllText(home.File("appdata/runtime/state.json"), $$"""{"repo": {{System.Text.Json.JsonSerializer.Serialize(repo)}}}""");

        var layout = EstateLayout.Resolve(new EstateInputs(_ => null, home.Folder("elsewhere"), home.Path, appData));

        Assert.Equal((Path.Join(Path.GetFullPath(home.Path), "Work"), repo), (layout.CodeRoot, layout.Source));
    }

    [Fact]
    public void Isolated_sees_nothing_outside_home()
    {
        using var home = new TempDir();

        var layout = EstateLayout.Isolated(home.Path);

        Assert.Equal((Path.Combine(home.Path, "code"), (string?)null), (layout.CodeRoot, layout.Source));
    }

    [Fact]
    public void Another_home_ignores_a_checkout_in_this_one()
    {
        using var home = new TempDir();
        using var other = new TempDir();
        var rig = home.Folder("Work/personal/rig");
        var layout = EstateLayout.Resolve(Inputs(home.Path, new() { [RigCheckout.HomeVariable] = rig }));

        Assert.Same(layout, layout.ForHome(home.Path));
        Assert.Equal(Path.Combine(other.Path, "code"), layout.ForHome(other.Path).CodeRoot);
        Assert.Equal(Path.Combine(other.Path, "code"), EstateLayout.Isolated(home.Path).ForHome(other.Path).CodeRoot);
    }

    [Fact]
    public void Display_abbreviates_home()
    {
        using var home = new TempDir();
        var layout = EstateLayout.Isolated(home.Path);

        Assert.Equal("~/code/personal", layout.Display(layout.Personal));
    }

    private static EstateInputs Inputs(string home, Dictionary<string, string> environment, string? baseDirectory = null) =>
        new(name => environment.GetValueOrDefault(name), baseDirectory ?? home, home, Path.Join(home, "no-appdata"));
}
