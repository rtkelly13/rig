using System.CommandLine;
using Rig.Cli.Forwarding;
using Rig.Cli.Latex;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Latex;

/// <summary>Ported case for case from the Python <c>latex</c> entry point, which had no tests of its own.</summary>
public sealed class LatexCommandTests : IDisposable
{
    private static readonly Ran Fine = new(0, string.Empty, string.Empty);
    private static readonly Ran Failed = new(1, string.Empty, string.Empty);

    private readonly TempDir _root = new TempDir().AsCodeRoot();
    private readonly FakeProc _proc = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Dictionary<string, string> _onPath = new(StringComparer.Ordinal);
    private string _path = "/usr/bin";
    private string? _cwd;

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _root.Dispose();
    }

    private string Blog => Path.Combine(_root.Path, "personal", "blog");

    private LatexCommand Command() =>
        new(_proc, new LatexHost(p => _onPath.GetValueOrDefault(p), _path, _root.Path, _cwd ?? _root.Path), _out, _err);

    private Task<int> Run(params string[] args) => Command().Build().Parse(args).InvokeAsync(
        new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);

    private void OnPath(params string[] programs)
    {
        foreach (var program in programs)
        {
            _onPath[program] = _root.File($"bin/{program}");
        }
    }

    private ProcRequest[] Requests(string program) => [.. _proc.Requests.Where(r => r.Argv[0] == program)];

    [Fact]
    public async Task Doctor_reports_every_tool_and_says_ready_when_the_required_ones_are_there()
    {
        OnPath("xelatex", "tlmgr");

        Assert.Equal(0, await Run("latex", "doctor"));

        var text = _out.ToString();
        Assert.Contains($"xelatex  OK      {_onPath["xelatex"]}", text, StringComparison.Ordinal);
        Assert.Contains("latexmk  MISSING not found (Automated TeX build tool)", text, StringComparison.Ordinal);
        Assert.Contains("ready", text, StringComparison.Ordinal);
        Assert.Empty(_proc.Requests);
    }

    [Theory]
    [InlineData("xelatex")]
    [InlineData("tlmgr")]
    public async Task Doctor_points_at_setup_when_a_required_tool_is_missing_but_still_exits_0(string present)
    {
        OnPath(present, "latexmk", "brew");

        Assert.Equal(0, await Run("latex", "doctor"));

        Assert.Contains("Tip: run `rig latex setup`", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_needs_homebrew()
    {
        Assert.Equal(1, await Run("latex", "setup"));

        Assert.Contains("Homebrew (brew) is required", _err.ToString(), StringComparison.Ordinal);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Setup_installs_basictex_then_the_packages_with_the_tex_folder_on_path()
    {
        OnPath("brew");
        _proc.Answer(Fine, "brew").Answer(Failed, "sudo", "tlmgr", "update").Answer(Fine, "sudo", "tlmgr", "install");

        Assert.Equal(0, await Run("latex", "setup"));

        Assert.Equal(["brew", "install", "--cask", "basictex"], Assert.Single(Requests("brew")).Argv);
        var sudo = Requests("sudo");
        Assert.Equal(["sudo", "tlmgr", "update", "--self"], sudo[0].Argv);
        Assert.Equal(["sudo", "tlmgr", "install", .. LatexCommand.Packages], sudo[1].Argv);
        Assert.All(_proc.Requests, r => Assert.True(r.Attached));
        Assert.All(sudo, r => Assert.Equal($"{LatexCommand.TexBin}{Path.PathSeparator}/usr/bin", r.Environment["PATH"]));
        Assert.Contains("All LaTeX packages installed.", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_skips_basictex_when_xelatex_is_there_and_keeps_a_path_that_has_the_tex_folder()
    {
        OnPath("brew", "xelatex");
        _path = $"/usr/bin{Path.PathSeparator}{LatexCommand.TexBin}";
        _proc.Answer(Fine, "sudo");

        Assert.Equal(0, await Run("latex", "setup"));

        Assert.Empty(Requests("brew"));
        Assert.All(Requests("sudo"), r => Assert.Equal(_path, r.Environment["PATH"]));
    }

    [Fact]
    public async Task Setup_stops_when_basictex_does_not_install()
    {
        OnPath("brew");
        _proc.Answer(Failed, "brew");

        Assert.Equal(1, await Run("latex", "setup"));

        Assert.Contains("brew install --cask basictex failed", _err.ToString(), StringComparison.Ordinal);
        Assert.Empty(Requests("sudo"));
    }

    [Fact]
    public async Task Setup_fails_when_the_packages_do_not_install()
    {
        OnPath("brew", "xelatex");
        _proc.Answer(Fine, "sudo").Answer(new Ran(5, string.Empty, string.Empty), "sudo", "tlmgr", "install");

        Assert.Equal(1, await Run("latex", "setup"));

        Assert.Contains("Package installation failed: tlmgr exited 5.", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_regenerates_the_tex_compiles_it_and_copies_the_pdf_into_the_blog()
    {
        OnPath("xelatex");
        var generator = _root.File("personal/blog/scripts/generate-cv-tex.mjs");
        _root.File("personal/blog/cv/template.tex");
        await File.WriteAllTextAsync(_root.File("personal/blog/cv/template.pdf"), "%PDF", TestContext.Current.CancellationToken);
        _proc.Answer(Fine, "node").Answer(Fine, _onPath["xelatex"]);

        Assert.Equal(0, await Run("latex", "build"));

        var node = Assert.Single(Requests("node"));
        Assert.Equal(["node", generator], node.Argv);
        Assert.Equal(Blog, node.WorkingDirectory);
        Assert.True(node.Attached);
        var xelatex = Assert.Single(Requests(_onPath["xelatex"]));
        Assert.Equal([_onPath["xelatex"], "-interaction=nonstopmode", "-halt-on-error", "template.tex"], xelatex.Argv);
        Assert.Equal(Path.Combine(Blog, "cv"), xelatex.WorkingDirectory);
        Assert.Equal("%PDF", await File.ReadAllTextAsync(Path.Combine(Blog, "public", "cv.pdf"), TestContext.Current.CancellationToken));
        Assert.Contains("Built and saved the PDF to", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_takes_the_file_and_output_relative_to_the_working_directory_and_can_skip_the_data()
    {
        OnPath("xelatex");
        _root.File("personal/blog/scripts/generate-cv-tex.mjs");
        _cwd = _root.Folder("work");
        await File.WriteAllTextAsync(_root.File("work/doc/cv.pdf"), "%PDF", TestContext.Current.CancellationToken);
        _proc.Answer(Fine, _onPath["xelatex"]);

        Assert.Equal(0, await Run("latex", "build", "-f", "doc/cv.tex", "-o", "out/final.pdf", "--no-sync-data"));

        Assert.Empty(Requests("node"));
        Assert.Equal(Path.Combine(_cwd, "doc"), Assert.Single(_proc.Requests).WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(_cwd, "out", "final.pdf")));
    }

    [Fact]
    public async Task Build_without_a_generator_compiles_and_copies_nothing_when_no_pdf_appears()
    {
        OnPath("xelatex");
        _proc.Answer(Fine, _onPath["xelatex"]);

        Assert.Equal(0, await Run("latex", "build", "--sync-data"));

        Assert.Empty(Requests("node"));
        Assert.False(File.Exists(Path.Combine(Blog, "public", "cv.pdf")));
    }

    [Fact]
    public async Task Build_stops_when_the_generator_fails()
    {
        OnPath("xelatex");
        _root.File("personal/blog/scripts/generate-cv-tex.mjs");
        _proc.Answer(Failed, "node");

        Assert.Equal(1, await Run("latex", "build"));

        Assert.Contains("generate-cv-tex.mjs failed", _err.ToString(), StringComparison.Ordinal);
        Assert.Single(_proc.Requests);
    }

    [Fact]
    public async Task Build_needs_xelatex()
    {
        Assert.SkipWhen(File.Exists(Path.Combine(LatexCommand.TexBin, "xelatex")), "BasicTeX is installed on this machine.");

        Assert.Equal(1, await Run("latex", "build", "--no-sync-data"));

        Assert.Contains("xelatex was not found", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_compile_prints_the_end_of_the_log()
    {
        OnPath("xelatex");
        _proc.Answer(new Ran(1, new string('a', 100) + new string('z', 1500), string.Empty), _onPath["xelatex"]);

        Assert.Equal(1, await Run("latex", "build", "--no-sync-data"));

        var err = _err.ToString();
        Assert.Contains("XeLaTeX compilation failed:\n" + new string('z', 1500), err, StringComparison.Ordinal);
        Assert.DoesNotContain("az", err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_blog_is_found_from_the_working_directory_when_the_code_root_has_none()
    {
        using var other = new TempDir();
        _cwd = other.Folder("site");
        other.Folder("site/cv");

        Assert.Equal(_cwd, Command().BlogDirectory());

        other.File("site/package.json");
        Assert.Equal(_cwd, Command().BlogDirectory());

        _cwd = other.Folder("checkout");
        var sibling = other.Folder("blog");
        Assert.Equal(sibling, Command().BlogDirectory());
    }

    [Fact]
    public async Task Rig_serves_latex_itself()
    {
        OnPath("xelatex", "tlmgr");
        var app = new RigApp(
            new PythonRig(_proc, "/rig"), new FakeGitHub(), _ => Task.FromResult<RepoSlug?>(null), _out, _err, [Command().Build]);

        Assert.Equal(0, await app.RunAsync(["latex", "doctor"], TestContext.Current.CancellationToken));

        Assert.Empty(_proc.Requests);
        Assert.Contains("ready", _out.ToString(), StringComparison.Ordinal);
    }
}
