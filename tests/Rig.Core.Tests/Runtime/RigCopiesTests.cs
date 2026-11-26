using Rig.Core.Runtime;

namespace Rig.Core.Tests.Runtime;

/// <summary>
/// <see cref="RigCopies"/> over a temp home, runtime folder and fake <c>rig</c> files, with this platform's shim name
/// and PATHEXT. The incident behind it: a Python venv's <c>rig.exe</c> came first on PATH and shadowed the real rig.
/// </summary>
public sealed class RigCopiesTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private static readonly string? Extensions = Windows ? ".COM;.EXE;.BAT;.CMD" : null;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Home => Path.Join(_dir.Path, "home");

    private string RuntimeRoot => Path.Join(_dir.Path, "appdata", "runtime");

    private string Shim => RigShim.PathFor(Home, Windows);

    private string Current => RuntimeManager.Published(RuntimeManager.CurrentLink(RuntimeRoot), "rig", Windows);

    private static string Exe => Windows ? "rig.exe" : "rig";

    // A runnable file: an execute bit off Windows, where the extension is what counts.
    private static string Runnable(string file)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return file;
    }

    // A Python virtualenv holding a rig, as `uv sync` left it: pyvenv.cfg beside Scripts (bin off Windows).
    private string Venv(string name)
    {
        var venv = _dir.Folder(name);
        File.WriteAllText(Path.Join(venv, "pyvenv.cfg"), "home = /usr\n");
        return Runnable(Path.Join(venv, Windows ? "Scripts" : "bin", Exe));
    }

    private static string PathOf(params string[] files) =>
        string.Join(Path.PathSeparator, files.Select(file => Path.GetDirectoryName(file)!));

    private RigCopiesReport Check(string? path) => RigCopies.Check(path, Extensions, Home, RuntimeRoot, Windows);

    private static string Shown(string path) => path.Replace('\\', '/');

    [Fact]
    public void The_shim_first_and_alone_is_healthy()
    {
        var report = Check(PathOf(Runnable(Shim)));

        Assert.Equal([new RigCopy(Shim, RigCopyKind.Shim)], report.Copies);
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.Issues);
    }

    [Fact]
    public void The_current_runtime_copy_first_is_healthy_too()
    {
        Runnable(Shim);
        var report = Check(PathOf(Runnable(Current)));

        Assert.Equal(RigCopyKind.Current, Assert.Single(report.Copies).Kind);
        Assert.Equal(0, report.Issues);
    }

    [Fact]
    public void A_python_venv_ahead_of_the_shim_is_two_issues_naming_the_venv_to_delete()
    {
        var venv = Venv("old-rig/.venv");
        var report = Check(PathOf(venv, Runnable(Shim)));

        Assert.Equal([RigCopyKind.PythonVenv, RigCopyKind.Shim], report.Copies.Select(copy => copy.Kind));
        Assert.Equal(2, report.Issues);
        var scripts = Shown(Path.GetDirectoryName(venv)!);
        Assert.Contains(report.Findings, finding => finding.Issue && finding.Message.StartsWith($"`rig` runs {Shown(venv)} (old Python rig (venv)), not the installed rig. Put {Shown(Path.GetDirectoryName(Shim)!)} before {scripts}", StringComparison.Ordinal));
        Assert.Contains(report.Findings, finding => finding.Issue && finding.Message == $"{scripts} holds the old Python rig CLI, which no longer runs. Delete the virtualenv {Shown(Path.Join(_dir.Path, "old-rig", ".venv"))}, or remove {scripts} from PATH.");
    }

    [Fact]
    public void A_python_venv_behind_the_shim_is_still_an_issue()
    {
        var report = Check(PathOf(Runnable(Shim), Venv("venv")));

        var finding = Assert.Single(report.Findings);
        Assert.True(finding.Issue);
        Assert.Contains("old Python rig CLI", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pinned_runtime_copy_first_is_an_issue_because_it_never_follows_an_install()
    {
        Runnable(Shim);
        var pinned = Runnable(Path.Join(RuntimeRoot, "abcdef123456", "rig", Exe));

        var report = Check(PathOf(pinned, Shim));

        Assert.Equal([RigCopyKind.Pinned, RigCopyKind.Shim], report.Copies.Select(copy => copy.Kind));
        Assert.Equal(1, report.Issues);
        Assert.Contains(report.Findings, finding => !finding.Issue && finding.Message.StartsWith(Shown(Shim), StringComparison.Ordinal));
    }

    [Fact]
    public void Copies_further_down_are_informational()
    {
        var build = Runnable(Path.Join(_dir.Path, "src", "Rig.Cli", "bin", "Release", "net11.0", Exe));
        var other = Runnable(Path.Join(_dir.Path, "tools", Exe));

        var report = Check(PathOf(Runnable(Shim), build, other));

        Assert.Equal([RigCopyKind.Shim, RigCopyKind.BuildOutput, RigCopyKind.Other], report.Copies.Select(copy => copy.Kind));
        Assert.Equal(0, report.Issues);
        Assert.Equal(
            [
                $"{Shown(build)} (dotnet build output) is also on PATH but never runs as `rig`; nothing to do unless you call it by path.",
                $"{Shown(other)} (other) is also on PATH but never runs as `rig`; nothing to do unless you call it by path.",
            ],
            report.Findings.Select(finding => finding.Message));
    }

    [Fact]
    public void A_folder_listed_twice_is_one_copy()
    {
        Runnable(Shim);

        Assert.Single(Check(PathOf(Shim, Shim)).Copies);
    }

    [Fact]
    public void A_missing_shim_says_to_run_the_install()
    {
        var report = Check(PathOf(Shim));

        Assert.Empty(report.Copies);
        Assert.Equal($"The rig shim {Shown(Shim)} is missing. Run `rig runtime install` to write it.", Assert.Single(report.Findings).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_shim_whose_folder_is_not_on_path_says_to_add_it(string? path)
    {
        Runnable(Shim);

        var finding = Assert.Single(Check(path).Findings);

        Assert.True(finding.Issue);
        Assert.Equal($"{Shown(Path.GetDirectoryName(Shim)!)} is not on PATH, so the rig shim there never runs. Add it to PATH, ahead of any other rig.", finding.Message);
    }

    [Fact]
    public void A_shim_on_path_that_cannot_run_says_to_rewrite_it()
    {
        Assert.SkipWhen(Windows, "Windows runs files by extension, not by an execute bit.");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Shim)!);
        File.WriteAllText(Shim, string.Empty);

        var finding = Assert.Single(Check(PathOf(Shim)).Findings);

        Assert.Equal($"The rig shim {Shown(Shim)} is on PATH but cannot run. Run `rig runtime install` to rewrite it.", finding.Message);
    }

    [Theory]
    [InlineData("bin/Debug/net11.0", RigCopyKind.BuildOutput)]
    [InlineData("BIN/release", RigCopyKind.BuildOutput)]
    [InlineData("bin", RigCopyKind.Other)]
    [InlineData("Scripts", RigCopyKind.Other)]
    public void Classify_names_build_outputs_and_leaves_a_plain_bin_as_other(string folder, RigCopyKind kind) =>
        Assert.Equal(kind, RigCopies.Classify(Path.Join(_dir.Path, folder, "rig"), Shim, RuntimeRoot, Windows));

    [Fact]
    public void A_scripts_folder_is_a_venv_only_beside_pyvenv_cfg()
    {
        var scripts = _dir.Folder("venv/Scripts");

        Assert.False(RigCopies.PythonVenvScripts(scripts));
        File.WriteAllText(Path.Join(_dir.Path, "venv", "pyvenv.cfg"), string.Empty);
        Assert.True(RigCopies.PythonVenvScripts(scripts + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Every_kind_has_a_label() =>
        Assert.Equal(
            ["rig shim", "runtime copy (current)", "pinned runtime copy", "old Python rig (venv)", "dotnet build output", "other"],
            Enum.GetValues<RigCopyKind>().Select(kind => new RigCopy("rig", kind).Label));
}
