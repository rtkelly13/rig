using System.CommandLine;
using System.Text.Json;
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
/// <c>rig runtime</c>: the pinned copy of one commit that the scheduled tasks run from. Ported from
/// <c>desktop_manager.runtime_cli</c>, without rich's colours. Exit codes: 0 success, 1 the install, rollback or
/// status failed, 2 there is no checkout to install from.
/// </summary>
/// <param name="host">Where the copies live and which checkout they come from.</param>
/// <param name="output">Where the report goes.</param>
internal sealed class RuntimeCommand(RuntimeHost host, TextWriter output)
{
    /// <summary>The <c>runtime</c> command, with install, rollback and status.</summary>
    public Command Build()
    {
        var statusRef = RefOption("Ref to compare against, as of the repo's last fetch.");
        var status = new Command("status", "The copy in use, the one kept for rollback, and how far the current one is behind --ref.") { statusRef };
        status.SetAction(parsed => Status(parsed.GetValue(statusRef)!));

        var installRef = RefOption("Commit to install. origin/* refs are fetched first.");
        var repo = new Option<string?>("--repo")
        {
            Description = "Checkout to take the commit from. Default: this one, or the one last installed from.",
        };
        var fetch = new Option<bool>("--fetch")
        {
            Description = "Fetch origin before resolving an origin/* ref (the default).",
        };
        var noFetch = new Option<bool>("--no-fetch") { Description = "Resolve the ref locally, without fetching." };
        var install = new Command(
            "install",
            "Build a copy of one commit, check it runs, then make it the one the scheduled tasks use.\n\n"
            + "Built from the commit itself, never the working tree, so uncommitted edits and the checked-out branch have no effect. "
            + "The copy it replaces is kept for rollback.") { installRef, repo, fetch, noFetch };
        install.SetAction(parsed => Install(parsed.GetValue(installRef)!, parsed.GetValue(repo), !parsed.GetValue(noFetch)));

        var rollback = new Command("rollback", "Point the scheduled tasks back at the previous copy (and keep the current one as the new previous).");
        rollback.SetAction(_ => Rollback());

        return new Command("runtime", "The pinned copy of this repo that the scheduled tasks run from.")
        {
            status,
            install,
            rollback,
        };
    }

    private static Option<string> RefOption(string description) =>
        new("--ref") { Description = description, DefaultValueFactory = _ => "origin/main" };

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

    private void ReportShim()
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

    // The rig checkout containing `start`: its pyproject.toml names the project, as find_checkout did in Python.
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
