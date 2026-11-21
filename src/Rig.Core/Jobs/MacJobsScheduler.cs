using Rig.Core.Processes;
using Rig.Core.Services;

namespace Rig.Core.Jobs;

/// <summary>One on-demand launchd coordinator: a tick at login and every fifteen minutes, with no resident poller.</summary>
public sealed class MacJobsScheduler(
    IProc proc,
    string home,
    string jobsDir,
    string codeRoot,
    string pathValue,
    Func<CancellationToken, Task<string>> domain)
{
    /// <summary>The per-user coordinator label.</summary>
    public const string Label = "dev.rtkelly.rig.jobs";

    /// <summary>The coordinator's LaunchAgent property list.</summary>
    public string PlistPath => Path.Join(Launchd.AgentsDirUnder(home), Label + ".plist");

    /// <summary>Render a finite tick; launchd starts it at login and periodically, never in a KeepAlive loop.</summary>
    public byte[] Render(JobsInstallTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Plist.Render(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Label"] = Label,
            ["ProgramArguments"] = new[] { target.Rig, "jobs", "run", "--trigger", "tick", "--scheduled" },
            ["WorkingDirectory"] = target.Root,
            ["RunAtLoad"] = true,
            ["StartInterval"] = Job.TickMinutes * 60,
            ["ProcessType"] = "Background",
            ["Nice"] = 10,
            ["LowPriorityIO"] = true,
            ["StandardOutPath"] = Path.Join(jobsDir, "launchd.log"),
            ["StandardErrorPath"] = Path.Join(jobsDir, "launchd-error.log"),
            ["EnvironmentVariables"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["HOME"] = home,
                ["PATH"] = pathValue,
                ["CODE_ROOT"] = codeRoot,
                [JobsSite.TraceHomeVariable] = Path.Join(Path.GetDirectoryName(jobsDir)!, "traces"),
            },
        });
    }

    /// <summary>Validate the executable before writing or unloading any existing registration.</summary>
    public async Task InstallAsync(JobsInstallTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!File.Exists(target.Rig))
        {
            throw new InvalidOperationException($"Published rig is missing: {target.Rig}. No LaunchAgent was changed.");
        }

        var plist = Render(target);
        var adapter = await AdapterAsync(cancellationToken).ConfigureAwait(false);
        if (File.Exists(PlistPath) && (await File.ReadAllBytesAsync(PlistPath, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(plist)
            && await adapter.LoadedAsync(Label, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        Directory.CreateDirectory(jobsDir);
        await adapter.LoadAsync(PlistPath, plist, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Remove only this coordinator's registration.</summary>
    public async Task<bool> UninstallAsync(CancellationToken cancellationToken = default) =>
        await (await AdapterAsync(cancellationToken).ConfigureAwait(false)).RemoveAsync(PlistPath, cancellationToken).ConfigureAwait(false);

    /// <summary>Whether the plist exists and launchd has loaded it; finite jobs need not have a live PID between ticks.</summary>
    public async Task<bool> RegisteredAsync(CancellationToken cancellationToken = default) =>
        File.Exists(PlistPath) && await (await AdapterAsync(cancellationToken).ConfigureAwait(false)).LoadedAsync(Label, cancellationToken).ConfigureAwait(false);

    private async Task<Launchd> AdapterAsync(CancellationToken cancellationToken) =>
        new(proc, Launchd.AgentsDirUnder(home), await domain(cancellationToken).ConfigureAwait(false));
}
