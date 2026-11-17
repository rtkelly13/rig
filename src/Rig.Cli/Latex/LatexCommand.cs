using System.CommandLine;
using DotMake.CommandLine;
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
/// <param name="proc">Runs the tools.</param>
/// <param name="host">Where the programs and the blog are.</param>
/// <param name="output">Where progress goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "latex", Description = "Manage and compile LaTeX documents locally with XeLaTeX.")]
internal sealed class LatexCommand(IProc proc, LatexHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal LatexCommand()
        : this(null!, null!, TextWriter.Null, TextWriter.Null)
    {
    }

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
        var latex = DeclarativeCommand.Build(this);
        latex.Add(DeclarativeCommand.Build(new DoctorCommand(host, output)));
        latex.Add(DeclarativeCommand.Build(new SetupCommand(proc, host, output, error)));
        latex.Add(DeclarativeCommand.Build(new BuildCommand(proc, host, output, error)));
        return latex;
    }

    /// <summary>A group named without one of its verbs is the parser's error.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(error);

    /// <summary>The blog checkout: <c>personal/blog</c> under the code root, else the working directory or a sibling.</summary>
    public string BlogDirectory() => BlogDirectory(host);

    private static string BlogDirectory(LatexHost host)
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

    private static Task<Ran> AttachedAsync(
        IProc proc, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, string? folder = null) =>
        proc.RunAsync(
            new ProcRequest(argv) { Attached = true, WorkingDirectory = folder, Environment = environment ?? new Dictionary<string, string>() },
            cancellationToken);

    /// <summary><c>rig latex doctor</c>: what is installed and what is missing.</summary>
    [CliCommand(Name = "doctor", Description = "Diagnose the local LaTeX / XeLaTeX toolchain.")]
    internal sealed class DoctorCommand(LatexHost host, TextWriter output)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal DoctorCommand()
            : this(null!, TextWriter.Null)
        {
        }

        /// <summary>One line per tool, then a verdict; always exit 0.</summary>
        public async Task<int> RunAsync(CliContext context)
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
    }

    /// <summary><c>rig latex setup</c>: BasicTeX and the packages, through Homebrew.</summary>
    [CliCommand(Name = "setup", Description = "Install BasicTeX and the LaTeX packages the CV needs, on macOS via Homebrew.")]
    internal sealed class SetupCommand(IProc proc, LatexHost host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal SetupCommand()
            : this(null!, null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Installs what is missing; stops at the first failure.</summary>
        public async Task<int> RunAsync(CliContext context)
        {
            var cancellationToken = context.CancellationToken;
            if (host.Find("brew") is null)
            {
                await error.WriteLineAsync("Error: Homebrew (brew) is required to provision BasicTeX on macOS.").ConfigureAwait(false);
                return (int)ExitCode.Drift;
            }

            if (host.Find("xelatex") is null)
            {
                await output.WriteLineAsync("Installing basictex via Homebrew...").ConfigureAwait(false);
                if (!(await AttachedAsync(proc, ["brew", "install", "--cask", "basictex"], null, cancellationToken).ConfigureAwait(false)).Ok)
                {
                    await error.WriteLineAsync("Error: brew install --cask basictex failed.").ConfigureAwait(false);
                    return (int)ExitCode.Drift;
                }
            }

            // A fresh BasicTeX is not on this shell's PATH yet, so tlmgr is looked up with its folder added.
            var path = host.Path.Split(System.IO.Path.PathSeparator).Contains(TexBin) ? host.Path : $"{TexBin}{System.IO.Path.PathSeparator}{host.Path}";
            var environment = new Dictionary<string, string> { ["PATH"] = path };

            await output.WriteLineAsync("Updating the tlmgr package database...").ConfigureAwait(false);
            _ = await AttachedAsync(proc, ["sudo", "tlmgr", "update", "--self"], environment, cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync($"Installing the LaTeX packages: {string.Join(", ", Packages)}").ConfigureAwait(false);
            var installed = await AttachedAsync(proc, ["sudo", "tlmgr", "install", .. Packages], environment, cancellationToken).ConfigureAwait(false);
            if (!installed.Ok)
            {
                await error.WriteLineAsync($"Package installation failed: tlmgr exited {installed.Code}.").ConfigureAwait(false);
                return (int)ExitCode.Drift;
            }

            await output.WriteLineAsync("All LaTeX packages installed.").ConfigureAwait(false);
            return (int)ExitCode.Clean;
        }
    }

    /// <summary><c>rig latex build</c>: the CV PDF, compiled the way the blog's workflow does.</summary>
    [CliCommand(Name = "build", Description = "Compile the CV with XeLaTeX, exactly as the blog's GitHub workflow does.")]
    internal sealed class BuildCommand(IProc proc, LatexHost host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal BuildCommand()
            : this(null!, null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>The .tex file to compile (default: blog/cv/template.tex).</summary>
        [CliOption(Name = "--file", Aliases = ["-f"], Required = false, Description = "The .tex file to compile (default: blog/cv/template.tex).")]
        public string? TexFile { get; set; }

        /// <summary>Where the PDF goes (default: blog/public/cv.pdf).</summary>
        [CliOption(Name = "--output", Aliases = ["-o"], Required = false, Description = "Where the PDF goes (default: blog/public/cv.pdf).")]
        public string? Target { get; set; }

        /// <summary>Regenerate template.tex from data/about/*.json first. This is what build always does.</summary>
        [CliOption(Name = "--sync-data", Description = "Regenerate template.tex from data/about/*.json first (the default).")]
        public bool SyncData { get; set; }

        /// <summary>Compile template.tex as it is.</summary>
        [CliOption(Name = "--no-sync-data", Description = "Compile template.tex as it is.")]
        public bool NoSyncData { get; set; }

        /// <summary>Compiles and, when XeLaTeX wrote one, copies the PDF into place.</summary>
        public async Task<int> RunAsync(CliContext context)
        {
            var cancellationToken = context.CancellationToken;
            var blog = BlogDirectory(host);
            var tex = System.IO.Path.GetFullPath(TexFile ?? System.IO.Path.Combine(blog, "cv", "template.tex"), host.WorkingDirectory);
            var pdf = System.IO.Path.GetFullPath(Target ?? System.IO.Path.Combine(blog, "public", "cv.pdf"), host.WorkingDirectory);

            if (!NoSyncData)
            {
                await output.WriteLineAsync("Step 1: regenerating template.tex from data/about/...").ConfigureAwait(false);
                var generator = System.IO.Path.Combine(blog, "scripts", "generate-cv-tex.mjs");
                if (File.Exists(generator)
                    && !(await AttachedAsync(proc, ["node", generator], null, cancellationToken, blog).ConfigureAwait(false)).Ok)
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
    }
}
