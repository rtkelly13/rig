using System.CommandLine;
using System.Text.Json;
using DotMake.CommandLine;
using Rig.Core.Processes;
using Rig.Core.Runtime;

namespace Rig.Cli.Runtime;

/// <summary>Where <c>rig runtime</c> keeps its copies and which checkout it installs from.</summary>
/// <param name="Root">The runtime folder, normally <c>&lt;app data&gt;/runtime</c>.</param>
/// <param name="Checkout">The checkout this CLI runs from, or the runtime copy it runs from.</param>
/// <param name="Home">The home folder the <c>rig</c> shim goes under.</param>
/// <param name="Windows">Whether the shim is <c>rig.cmd</c> and links are junctions.</param>
/// <param name="Proc">Runs git, uv, dotnet and the copy's own checks.</param>
/// <param name="Clock">The clock <c>installed.json</c> stamps its time with.</param>
internal sealed record RuntimeHost(string Root, string Checkout, string Home, bool Windows, IProc Proc, TimeProvider Clock);

/// <summary>
/// <c>rig runtime</c>: the pinned copy of one commit that the scheduled tasks run from. Exit codes: 0 success, 1 the install, rollback or
/// status failed, 2 there is no checkout to install from.
/// </summary>
/// <param name="host">Where the copies live and which checkout they come from.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where the parser's answer to a bare <c>runtime</c> goes.</param>
[CliCommand(Name = "runtime", Description = "The pinned copy of this repo that the scheduled tasks run from.")]
internal sealed class RuntimeCommand(RuntimeHost host, TextWriter output, TextWriter? error = null)
{
    /// <summary>The ref status and install default to.</summary>
    internal const string DefaultRef = "origin/main";

    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal RuntimeCommand()
        : this(null!, TextWriter.Null)
    {
    }

    /// <summary>The <c>runtime</c> command, with install, rollback and status.</summary>
    public Command Build()
    {
        var runtime = DeclarativeCommand.Build(this);
        runtime.Add(DeclarativeCommand.Build(new StatusCommand(this)));
        runtime.Add(DeclarativeCommand.Build(new InstallCommand(this)));
        runtime.Add(DeclarativeCommand.Build(new RollbackCommand(this)));
        return runtime;
    }

    /// <summary>A group named without one of its verbs is the parser's error.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(Error);

    /// <summary><c>rig runtime status</c>.</summary>
    [CliCommand(Name = "status", Description = "The copy in use, the one kept for rollback, and how far the current one is behind --ref.")]
    internal sealed class StatusCommand(RuntimeCommand owner)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal StatusCommand()
            : this(null!)
        {
        }

        /// <summary>The ref to compare against.</summary>
        [CliOption(Name = "--ref", Description = "Ref to compare against, as of the repo's last fetch.")]
        public string Ref { get; set; } = DefaultRef;

        /// <summary>Reports the copy in use.</summary>
        public int Run() => owner.Status(Ref);
    }

    /// <summary><c>rig runtime install</c>.</summary>
    [CliCommand(
        Name = "install",
        Description = "Build a copy of one commit, check it runs, then make it the one the scheduled tasks use.\n\n"
            + "Built from the commit itself, never the working tree, so uncommitted edits and the checked-out branch have no effect. "
            + "The copy it replaces is kept for rollback.")]
    internal sealed class InstallCommand(RuntimeCommand owner)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal InstallCommand()
            : this(null!)
        {
        }

        /// <summary>The commit to install.</summary>
        [CliOption(Name = "--ref", Description = "Commit to install. origin/* refs are fetched first.")]
        public string Ref { get; set; } = DefaultRef;

        /// <summary>The checkout to take the commit from.</summary>
        [CliOption(Name = "--repo", Required = false, Description = "Checkout to take the commit from. Default: this one, or the one last installed from.")]
        public string? Repo { get; set; }

        /// <summary>Fetch origin first, the default.</summary>
        [CliOption(Name = "--fetch", Description = "Fetch origin before resolving an origin/* ref (the default).")]
        public bool Fetch { get; set; }

        /// <summary>Resolve the ref locally.</summary>
        [CliOption(Name = "--no-fetch", Description = "Resolve the ref locally, without fetching.")]
        public bool NoFetch { get; set; }

        /// <summary>Builds the copy and makes it current.</summary>
        public int Run() => owner.Install(Ref, Repo, !NoFetch);
    }

    /// <summary><c>rig runtime rollback</c>.</summary>
    [CliCommand(Name = "rollback", Description = "Point the scheduled tasks back at the previous copy (and keep the current one as the new previous).")]
    internal sealed class RollbackCommand(RuntimeCommand owner)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal RollbackCommand()
            : this(null!)
        {
        }

        /// <summary>Points the tasks back at the previous copy.</summary>
        public int Run() => owner.Rollback();
    }

    private TextWriter Error => error ?? TextWriter.Null;

    private RuntimeManager Manager => new(host.Proc, host.Root, host.Clock);

    private int Status(string refName)
    {
        try
        {
            var status = Manager.Status(refName);
            if (status.State.Current is not { Length: > 0 } current)
            {
                output.WriteLine($"Nothing installed in {host.Root}: run `rig runtime install`.");
                return 1;
            }

            output.WriteLine($"Current:  {current}  {status.Current?.Ref ?? "?"}  {status.Current?.InstalledAt ?? "?"}");
            if (status.State.Previous is { Length: > 0 } previous)
            {
                output.WriteLine($"Previous: {previous}  {status.Previous?.Ref ?? "?"}  {status.Previous?.InstalledAt ?? "?"}");
            }

            output.WriteLine($"Path:     {status.Link}");
            if (status.Behind is { } behind)
            {
                output.WriteLine($"Behind {refName}: {behind} commit(s)");
            }

            return 0;
        }
        catch (Exception error) when (error is RuntimeInstallException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            output.WriteLine($"Status failed: {error.Message}");
            return 1;
        }
    }

    private int Install(string refName, string? explicitRepo, bool fetch)
    {
        try
        {
            if (ResolveRepo(explicitRepo) is not { } repo)
            {
                output.WriteLine("No repo to install from: pass --repo <rig checkout>.");
                return 2;
            }

            var result = Manager.Install(repo, refName, fetch, line => output.WriteLine(line));
            var link = RuntimeManager.CurrentLink(host.Root);
            var sha = result.Sha[..Math.Min(result.Sha.Length, RuntimeManager.ShortShaLength)];
            if (!result.Changed)
            {
                output.WriteLine($"Already current: {sha} ({refName}) at {link}");
            }
            else
            {
                output.WriteLine($"Current: {sha} ({refName}) at {link}");
                if (result.Previous is { Length: > 0 } previous)
                {
                    output.WriteLine($"Previous kept for rollback: {previous}");
                }

                foreach (var name in result.Removed)
                {
                    output.WriteLine($"Removed old copy: {name}");
                }
            }

            ReportShim();
            return 0;
        }
        catch (Exception error) when (error is RuntimeInstallException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            output.WriteLine($"Install failed: {error.Message}");
            return 1;
        }
    }

    private int Rollback()
    {
        try
        {
            var state = Manager.Rollback();
            output.WriteLine($"Current: {state.Current} (previous: {state.Previous})");
            return 0;
        }
        catch (Exception error) when (error is RuntimeInstallException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            output.WriteLine($"Rollback failed: {error.Message}");
            return 1;
        }
    }

    internal void ReportShim()
    {
        var path = RigShim.PathFor(host.Home, host.Windows);
        var shown = path.Replace('\\', '/');
        try
        {
            switch (RigShim.Install(path, host.Root, host.Windows))
            {
                case ShimOutcome.Written:
                    output.WriteLine($"rig on PATH: {shown} runs the current copy");
                    break;
                case ShimOutcome.Unchanged:
                    output.WriteLine($"rig on PATH: {shown}");
                    break;
                default:
                    output.WriteLine($"Left {shown} alone: rig did not write it, so `rig` may not be this copy.");
                    break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"rig on PATH: could not write {shown}: {error.Message}");
        }
    }

    // The repo to install from: the explicit one, this checkout when it is not a runtime copy, or the one last installed from.
    private string? ResolveRepo(string? explicitRepo)
    {
        if (explicitRepo is not null)
        {
            return explicitRepo;
        }

        var manager = Manager;
        if (!RuntimeManager.RunningCopy(host.Checkout, host.Root) && FindCheckout(host.Checkout) is { } checkout)
        {
            return checkout;
        }

        return manager.ReadState().Repo is { Length: > 0 } recorded ? recorded : null;
    }

    // The rig checkout containing `start`: its pyproject.toml names the project.
    internal static string? FindCheckout(string start)
    {
        for (var directory = Path.GetFullPath(start); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var pyproject = Path.Join(directory, "pyproject.toml");
            if (!File.Exists(pyproject))
            {
                continue;
            }

            var text = File.ReadAllText(pyproject);
            if (text.Contains("name = \"rig\"", StringComparison.Ordinal) || text.Contains("name = \"shared-utilities\"", StringComparison.Ordinal))
            {
                return directory;
            }
        }

        return null;
    }
}
