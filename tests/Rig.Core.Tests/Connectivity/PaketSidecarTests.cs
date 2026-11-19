using Rig.Core.Connectivity;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Connectivity;

/// <summary>
/// The Paket sidecar against the real <c>Rig.Paket</c> executable the build copies beside the tests, on Paket files
/// that use the syntax rig's own never did: groups, both comment styles, inline options and remote files.
/// </summary>
public sealed class PaketSidecarTests : IDisposable
{
    private readonly TempDir _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PaketSidecar Real => new(new SystemProc());

    public void Dispose() => _temp.Dispose();

    private string Write(string relative, string text)
    {
        var path = _temp.File(relative);
        File.WriteAllText(path, text);
        return path;
    }

    private static Dictionary<string, string?> Versions(PaketFile file, string group) =>
        file.Groups.Single(g => g.Name == group).Packages.ToDictionary(p => p.Name, p => p.Version, StringComparer.Ordinal);

    [Fact]
    public async Task The_sidecar_is_built_beside_the_tests()
    {
        Assert.True(File.Exists(PaketSidecar.DefaultPath), PaketSidecar.DefaultPath);
        Assert.Equal(Path.Join(AppContext.BaseDirectory, "paket"), Path.GetDirectoryName(PaketSidecar.DefaultPath));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Dependencies_read_every_group_with_their_constraints()
    {
        var path = Write("paket.dependencies", """
            source https://api.nuget.org/v3/index.json
            // a line comment
            # a hash comment
            storage: none
            framework: net10.0, net11.0

            nuget Pinned == 1.2.3 // trailing comment
            nuget Floor >= 2.0 framework: net10.0
            nuget Tilde ~> 3.1
            nuget Bare
            github fsprojects/Paket src/Paket.Core/Utils.fs
            http https://example.com/tool.zip tool.zip

            group Test
              source https://api.nuget.org/v3/index.json
              nuget Other 5.0.0
            """);

        var reading = await Real.ReadAsync([path], Token);

        var file = Assert.Single(reading.Files);
        Assert.Null(reading.Unavailable);
        Assert.Null(file.Error);
        Assert.Equal(["Main", "Test"], file.Groups.Select(g => g.Name));
        var main = Versions(file, "Main");
        Assert.Equal(["Pinned", "Floor", "Tilde", "Bare"], main.Keys);
        Assert.Equal("1.2.3", main["Pinned"]?.TrimStart('=', ' '));
        Assert.Equal(">= 2.0", main["Floor"]);
        Assert.Equal(">= 3.1 < 4.0", main["Tilde"]);
        Assert.Equal(string.Empty, main["Bare"]);
        Assert.Equal("5.0", Versions(file, "Test")["Other"]);
    }

    [Fact]
    public async Task A_lock_beside_the_dependencies_gives_the_resolved_versions()
    {
        var path = Write("paket.dependencies", "source https://api.nuget.org/v3/index.json\nstorage: none\nnuget Alpha >= 1.0\n");
        _ = Write("paket.lock", "STORAGE: NONE\nNUGET\n  remote: https://api.nuget.org/v3/index.json\n    Alpha (1.4.2)\n");

        var file = Assert.Single((await Real.ReadAsync([path], Token)).Files);

        Assert.Null(file.Warning);
        Assert.Equal("1.4.2", Versions(file, "Main")["Alpha"]);
    }

    [Fact]
    public async Task An_unreadable_lock_is_a_warning_and_the_constraints_stand()
    {
        var path = Write("paket.dependencies", "source https://api.nuget.org/v3/index.json\nnuget Alpha >= 1.0\n");
        _ = Write("paket.lock", "NUGET\n  remote: ((\n    Alpha (not a version\n");

        var file = Assert.Single((await Real.ReadAsync([path], Token)).Files);

        Assert.Null(file.Error);
        Assert.StartsWith("paket.lock was not used:", file.Warning, StringComparison.Ordinal);
        Assert.Equal(">= 1.0", Versions(file, "Main")["Alpha"]);
    }

    [Fact]
    public async Task References_read_package_names_per_group_and_leave_files_out()
    {
        var path = Write("app/paket.references", """
            // a comment
            Alpha
            Beta framework: net10.0
            copy_local_package copy_local: false
            File: Utils.fs Common/Utils.fs

            group Test
            Gamma
            """);

        var file = Assert.Single((await Real.ReadAsync([path], Token)).Files);

        Assert.Null(file.Error);
        Assert.Equal(["Main", "Test"], file.Groups.Select(g => g.Name));
        Assert.Equal(["Alpha", "Beta", "copy_local_package"], file.Groups[0].Packages.Select(p => p.Name));
        Assert.Equal(["Gamma"], file.Groups[1].Packages.Select(p => p.Name));
        Assert.All(file.Groups.SelectMany(g => g.Packages), p => Assert.Null(p.Version));
    }

    [Fact]
    public async Task One_bad_file_is_its_own_error_and_the_rest_are_read()
    {
        var bad = Write("bad/paket.dependencies", "garbage ((\n");
        var missing = Path.Join(_temp.Path, "gone", "paket.references");
        var good = Write("good/paket.references", "Alpha\n");
        var notPaket = Write("other.txt", "x");

        var reading = await Real.ReadAsync([bad, missing, good, notPaket], Token);

        Assert.Equal([bad, missing, good, notPaket], reading.Files.Select(f => f.Path));
        Assert.Contains("garbage", reading.Files[0].Error, StringComparison.Ordinal);
        Assert.NotNull(reading.Files[1].Error);
        Assert.Null(reading.Files[2].Error);
        Assert.Equal(["Alpha"], reading.Files[2].Groups.Single().Packages.Select(p => p.Name));
        Assert.StartsWith("Not a Paket file", reading.Files[3].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_over_the_manifest_bound_is_an_error()
    {
        var path = Write("big/paket.references", new string(' ', (int)Manifests.MaxBytes + 1));

        var file = Assert.Single((await Real.ReadAsync([path], Token)).Files);

        Assert.Equal("Manifest exceeds 2 MB", file.Error);
    }

    [Fact]
    public async Task A_missing_sidecar_is_unavailable_not_a_crash()
    {
        var proc = new FakeProc();

        var reading = await new PaketSidecar(proc, Path.Join(_temp.Path, "paket", PaketSidecar.FileName(windows: false))).ReadAsync(["/x/paket.references"], Token);

        Assert.Empty(reading.Files);
        Assert.Contains("not installed", reading.Unavailable, StringComparison.Ordinal);
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task A_sidecar_that_fails_is_unavailable_with_its_reason()
    {
        var exe = _temp.File("sidecar/Rig.Paket");
        var proc = new FakeProc().Answer(new Ran(3, string.Empty, "boom"), exe);

        var reading = await new PaketSidecar(proc, exe).ReadAsync(["/x/paket.references"], Token);

        Assert.Equal($"the Paket sidecar exited with 3: boom", reading.Unavailable);
        Assert.Equal("/x/paket.references", Assert.Single(proc.Requests).StdinText);
    }

    [Fact]
    public async Task A_sidecar_that_answers_nonsense_is_unavailable()
    {
        var exe = _temp.File("sidecar/Rig.Paket");
        var proc = new FakeProc().Answer(new Ran(0, "not json", string.Empty), exe);

        var reading = await new PaketSidecar(proc, exe).ReadAsync(["/x/paket.references"], Token);

        Assert.StartsWith("the Paket sidecar answered something unreadable:", reading.Unavailable, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "Rig.Paket.exe")]
    [InlineData(false, "Rig.Paket")]
    public void The_sidecar_is_an_exe_on_windows(bool windows, string name) => Assert.Equal(name, PaketSidecar.FileName(windows));
}
