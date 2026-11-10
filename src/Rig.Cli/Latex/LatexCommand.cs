using System.CommandLine;
using Rig.Core;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Latex;

/// <summary>Where <c>rig latex</c> looks for things: programs on PATH, the code root and the working directory.</summary>
/// <param name="Find">Finds a program on PATH, or returns null.</param>
/// <param name="Path">This process's PATH value, which <c>setup</c> extends with the TeX folder.</param>
/// <param name="CodeRoot">The code root; the blog is <c>personal/blog</c> under it.</param>
/// <param name="WorkingDirectory">The folder the command was run from.</param>
internal sealed record LatexHost(Func<string, string?> Find, string Path, string CodeRoot, string WorkingDirectory);

/// <summary>
/// <c>rig latex doctor|setup|build</c>: provisions XeLaTeX and builds the blog's CV PDF the way its GitHub workflow
/// does. Exit codes: 0 done (doctor always reports and exits 0), 1 a missing tool or a failed install or build.
/// </summary>
internal sealed class LatexCommand(IProc proc, LatexHost host, TextWriter output, TextWriter error)
{
    /// <summary>Where BasicTeX puts its programs on macOS.</summary>
    public const string TexBin = "/Library/TeX/texbin";

    /// <summary>The TeX Live packages <c>twentysecondcv.cls</c> needs, beyond BasicTeX.</summary>
    public static readonly IReadOnlyList<string> Packages =
    [
        "fontspec", "clearsans", "pstricks", "fontawesome", "pgf", "xcolor", "textpos", "ragged2e", "etoolbox",
        "ifmtarg", "marvosym", "parskip", "enumitem", "hyperref", "smartdiagram", "latexmk",
    ];

    private const int TailLength = 1500;

    private static readonly (string Program, string Purpose, bool Required)[] Tools =
    [
        ("xelatex", "Required for fontspec and twentysecondcv.cls", true),
        ("latexmk", "Automated TeX build tool", false),
        ("tlmgr", "TeXLive package manager", true),
        ("brew", "macOS package manager", false),
    ];

    /// <summary>The <c>rig latex</c> command group.</summary>
    public Command Build()
    {
        var latex = new Command("latex", "Manage and compile LaTeX documents locally with XeLaTeX.");
        var doctor = new Command("doctor", "Diagnose the local LaTeX / XeLaTeX toolchain.");
        doctor.SetAction((_, _) => DoctorAsync());
        var setup = new Command("setup", "Install BasicTeX and the LaTeX packages the CV needs, on macOS via Homebrew.");
        setup.SetAction((_, ct) => SetupAsync(ct));
        latex.Subcommands.Add(doctor);
        latex.Subcommands.Add(setup);
        latex.Subcommands.Add(BuildBuild());
        return latex;
    }

    /// <summary>The blog checkout: <c>personal/blog</c> under the code root, else the working directory or a sibling.</summary>
    public string BlogDirectory()
    {
        var candidate = System.IO.Path.Combine(host.CodeRoot, CodeRoot.PersonalFolder, "blog");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        var here = host.WorkingDirectory;
        if (Directory.Exists(System.IO.Path.Combine(here, "cv")) && File.Exists(System.IO.Path.Combine(here, "package.json")))
        {
            return here;
        }

        var sibling = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here) ?? here, "blog");
        return Directory.Exists(sibling) ? sibling : here;
    }

    private Command BuildBuild()
    {
        var file = new Option<string?>("--file", "-f") { Description = "The .tex file to compile (default: blog/cv/template.tex)." };
        var target = new Option<string?>("--output", "-o") { Description = "Where the PDF goes (default: blog/public/cv.pdf)." };
        var sync = new Option<bool>("--sync-data") { Description = "Regenerate template.tex from data/about/*.json first (the default)." };
        var noSync = new Option<bool>("--no-sync-data") { Description = "Compile template.tex as it is." };
        var build = new Command("build", "Compile the CV with XeLaTeX, exactly as the blog's GitHub workflow does.") { file, target, sync, noSync };
        build.SetAction((parsed, ct) => BuildAsync(parsed.GetValue(file), parsed.GetValue(target), !parsed.GetValue(noSync), ct));
        return build;
    }

    private async Task<int> DoctorAsync()
    {
        await output.WriteLineAsync("Local LaTeX environment").ConfigureAwait(false);
        var ready = true;
        foreach (var (program, purpose, required) in Tools)
        {
            var found = host.Find(program);
            var detail = found ?? $"not found ({purpose})";
            await output.WriteLineAsync($"  {program,-8} {(found is null ? "MISSING" : "OK"),-7} {detail}").ConfigureAwait(false);
            ready &= found is not null || !required;
        }

        await output.WriteLineAsync(
            ready
                ? "The local LaTeX environment is provisioned and ready."
                : "Tip: run `rig latex setup` to install BasicTeX and the packages via Homebrew.").ConfigureAwait(false);
        return (int)ExitCode.Clean;
    }

    private async Task<int> SetupAsync(CancellationToken cancellationToken)
    {
        if (host.Find("brew") is null)
        {
            await error.WriteLineAsync("Error: Homebrew (brew) is required to provision BasicTeX on macOS.").ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }

        if (host.Find("xelatex") is null)
        {
            await output.WriteLineAsync("Installing basictex via Homebrew...").ConfigureAwait(false);
            if (!(await AttachedAsync(["brew", "install", "--cask", "basictex"], null, cancellationToken).ConfigureAwait(false)).Ok)
            {
                await error.WriteLineAsync("Error: brew install --cask basictex failed.").ConfigureAwait(false);
                return (int)ExitCode.Drift;
            }
        }

        // A fresh BasicTeX is not on this shell's PATH yet, so tlmgr is looked up with its folder added.
        var path = host.Path.Split(System.IO.Path.PathSeparator).Contains(TexBin) ? host.Path : $"{TexBin}{System.IO.Path.PathSeparator}{host.Path}";
        var environment = new Dictionary<string, string> { ["PATH"] = path };

        await output.WriteLineAsync("Updating the tlmgr package database...").ConfigureAwait(false);
        _ = await AttachedAsync(["sudo", "tlmgr", "update", "--self"], environment, cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync($"Installing the LaTeX packages: {string.Join(", ", Packages)}").ConfigureAwait(false);
        var installed = await AttachedAsync(["sudo", "tlmgr", "install", .. Packages], environment, cancellationToken).ConfigureAwait(false);
        if (!installed.Ok)
        {
            await error.WriteLineAsync($"Package installation failed: tlmgr exited {installed.Code}.").ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }

        await output.WriteLineAsync("All LaTeX packages installed.").ConfigureAwait(false);
        return (int)ExitCode.Clean;
    }

    private async Task<int> BuildAsync(string? file, string? target, bool syncData, CancellationToken cancellationToken)
    {
        var blog = BlogDirectory();
        var tex = System.IO.Path.GetFullPath(file ?? System.IO.Path.Combine(blog, "cv", "template.tex"), host.WorkingDirectory);
        var pdf = System.IO.Path.GetFullPath(target ?? System.IO.Path.Combine(blog, "public", "cv.pdf"), host.WorkingDirectory);

        if (syncData)
        {
            await output.WriteLineAsync("Step 1: regenerating template.tex from data/about/...").ConfigureAwait(false);
            var generator = System.IO.Path.Combine(blog, "scripts", "generate-cv-tex.mjs");
            if (File.Exists(generator)
                && !(await AttachedAsync(["node", generator], null, cancellationToken, blog).ConfigureAwait(false)).Ok)
            {
                await error.WriteLineAsync("Error: generate-cv-tex.mjs failed.").ConfigureAwait(false);
                return (int)ExitCode.Drift;
            }
        }

        var xelatex = host.Find("xelatex") ?? System.IO.Path.Combine(TexBin, "xelatex");
        if (!File.Exists(xelatex))
        {
            await error.WriteLineAsync("Error: xelatex was not found. Run `rig latex setup` or install BasicTeX.").ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }

        var folder = System.IO.Path.GetDirectoryName(tex)!;
        await output.WriteLineAsync($"Step 2: compiling {System.IO.Path.GetFileName(tex)} with XeLaTeX...").ConfigureAwait(false);
        var ran = await proc.RunAsync(
            new ProcRequest([xelatex, "-interaction=nonstopmode", "-halt-on-error", System.IO.Path.GetFileName(tex)]) { WorkingDirectory = folder },
            cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            await error.WriteLineAsync($"XeLaTeX compilation failed:\n{ran.Out[Math.Max(0, ran.Out.Length - TailLength)..]}").ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }

        var compiled = System.IO.Path.Combine(folder, System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(tex), ".pdf"));
        if (File.Exists(compiled))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pdf)!);
            File.Copy(compiled, pdf, overwrite: true);
            await output.WriteLineAsync($"Built and saved the PDF to {pdf}").ConfigureAwait(false);
        }

        return (int)ExitCode.Clean;
    }

    private Task<Ran> AttachedAsync(
        IReadOnlyList<string> argv, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, string? folder = null) =>
        proc.RunAsync(
            new ProcRequest(argv) { Attached = true, WorkingDirectory = folder, Environment = environment ?? new Dictionary<string, string>() },
            cancellationToken);
}
