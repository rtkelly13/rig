using System.Globalization;
using System.Text;
using Rig.Core.Jobs;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Services;

namespace Rig.Core.Tools;

/// <summary>The platforms a rig service can be installed on.</summary>
public enum ServicePlatform
{
    /// <summary>A per-user LaunchAgent under <c>~/Library/LaunchAgents</c>.</summary>
    MacOS,

    /// <summary>A Task Scheduler task under <c>Rig</c>.</summary>
    Windows,

    /// <summary>A systemd user unit under <c>~/.config/systemd/user</c>.</summary>
    Linux,
}

/// <summary>One background service <c>rig tools</c> can install, ensure, uninstall and report.</summary>
/// <param name="Name">The command word: <c>web</c>, <c>mcp</c>, <c>desktop</c> or <c>collector</c>.</param>
/// <param name="DisplayName">The name Windows' Task Scheduler shows.</param>
/// <param name="DefaultPort">The port it listens on, or null for a service that does not listen.</param>
/// <param name="PreviewPort">The preview environment's port, when it has a separate one.</param>
/// <param name="LongLived">Whether the service should be kept alive rather than run once at sign-in.</param>
public sealed record ServiceTarget(string Name, string DisplayName, int? DefaultPort, int? PreviewPort, bool LongLived)
{
    /// <summary>The dashboard.</summary>
    public static readonly ServiceTarget Web = new("web", "Web Dashboard", 8090, 8091, true);

    /// <summary>The web host's HTTP MCP transport, on its own port so a client can connect to it.</summary>
    public static readonly ServiceTarget Mcp = new("mcp", "Mcp", 8092, 8092, true);

    /// <summary>The logon workspace restore.</summary>
    public static readonly ServiceTarget Desktop = new("desktop", "Desktop", null, null, false);

    /// <summary>The always-on OpenTelemetry collector.</summary>
    public static readonly ServiceTarget Collector = new("collector", "Collector", null, null, false);

    /// <summary>Every target, in help and <c>status</c> order.</summary>
    public static IReadOnlyList<ServiceTarget> All { get; } = [Web, Mcp, Desktop, Collector];

    /// <summary>The target named <paramref name="name"/>, or null when there is none.</summary>
    public static ServiceTarget? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.Ordinal));

    /// <summary>The port this target listens on: the explicit override, else the preview or production default.</summary>
    public int? Port(bool preview, int? explicitPort = null) => explicitPort ?? (preview ? PreviewPort : DefaultPort);

    /// <summary>The rig arguments that start this target.</summary>
    /// <param name="port">The port to pass, or null for a service without one.</param>
    /// <param name="preview">Whether this is the preview environment.</param>
    public IReadOnlyList<string> Arguments(int? port, bool preview) => Name switch
    {
        "web" => port is { } value
            ? ["web", "--port", value.ToString(CultureInfo.InvariantCulture), "--no-open", .. preview ? ["--preview"] : Array.Empty<string>()]
            : ["web", "--no-open", .. preview ? ["--preview"] : Array.Empty<string>()],
        "mcp" => port is { } mcpPort
            ? ["web", "--port", mcpPort.ToString(CultureInfo.InvariantCulture), "--no-open", .. preview ? ["--preview"] : Array.Empty<string>()]
            : ["web", "--no-open", .. preview ? ["--preview"] : Array.Empty<string>()],
        "desktop" => ["desktop", "workspace", "restore"],
        "collector" => ["otel-stack", "collector", "ensure"],
        _ => throw new InvalidOperationException($"unknown service target: {Name}"),
    };
}

/// <summary>The files, processes and clock a service manager uses, so tests never touch the host.</summary>
/// <param name="Home">The user's home folder: where LaunchAgents and systemd units go.</param>
/// <param name="WorkingDirectory">The folder the service starts in.</param>
/// <param name="RigCommand">This CLI's executable and any leading arguments, such as <c>dotnet</c> and the DLL.</param>
/// <param name="Platform">Which service manager to use.</param>
/// <param name="Proc">Runs <c>launchctl</c>, <c>schtasks</c> and <c>systemctl</c>.</param>
/// <param name="Probe">Asks whether a port accepts connections.</param>
/// <param name="Clock">The clock <c>ensure</c> waits against.</param>
/// <param name="Delay">Waits between probes; tests advance the clock instead.</param>
/// <param name="LaunchdDomain">The launchd domain, <c>gui/&lt;uid&gt;</c>; unused on other platforms.</param>
/// <param name="PathValue">The <c>PATH</c> the service inherits, as the installer's own; null omits it.</param>
/// <param name="UserId">The Windows user a logon task runs as, <c>DOMAIN\user</c>; unused on other platforms.</param>
/// <param name="ScratchDirectory">Where a task's XML is written for <c>schtasks /XML</c>; null uses the temp folder.</param>
/// <param name="Pythonw">
/// Finds the <c>pythonw</c> a Windows task starts rig through, so no window appears; null, or a null answer, starts rig
/// directly with <see cref="JobsInstallTarget.DetachFlag"/>.
/// </param>
public sealed record ServiceHost(
    string Home,
    string WorkingDirectory,
    IReadOnlyList<string> RigCommand,
    ServicePlatform Platform,
    IProc Proc,
    IPortProbe Probe,
    TimeProvider Clock,
    Func<TimeSpan, CancellationToken, Task> Delay,
    Func<CancellationToken, Task<string>> LaunchdDomain,
    string? PathValue = null,
    string? UserId = null,
    string? ScratchDirectory = null,
    Func<CancellationToken, Task<string?>>? Pythonw = null);

/// <summary>What an install or uninstall did, in the words the command prints.</summary>
/// <param name="Message">The one-line report.</param>
public sealed record ServiceResult(string Message);

/// <summary>What <c>rig tools status</c> found for one target.</summary>
/// <param name="Installed">Whether the plist, task or unit is registered.</param>
/// <param name="Running">Whether the service manager reports it running.</param>
/// <param name="Listening">Whether its port accepts connections; null when it has no port.</param>
/// <param name="Port">The port inspected, when it has one.</param>
public sealed record ServiceStatus(bool Installed, bool Running, bool? Listening, int? Port);

/// <summary>
/// Installs, ensures, uninstalls and reports the background services rig owns: a LaunchAgent on macOS, a logon task
/// on Windows, a systemd user unit on Linux.
/// </summary>
public sealed class ServiceManager(ServiceHost host)
{
    /// <summary>How often <c>ensure</c> checks the port while waiting.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Install and (re)start <paramref name="target"/>, replacing any earlier registration.</summary>
    /// <exception cref="ProcException">The platform's service manager refused the registration.</exception>
    public async Task<ServiceResult> InstallAsync(ServiceTarget target, bool preview, int? port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var effectivePort = target.Port(preview, port);
        var arguments = target.Arguments(effectivePort, preview);
        IReadOnlyList<string> command = [.. host.RigCommand, .. arguments];
        return host.Platform switch
        {
            ServicePlatform.MacOS => new ServiceResult(await InstallMacAsync(target, preview, effectivePort, command, cancellationToken).ConfigureAwait(false)),
            ServicePlatform.Windows => new ServiceResult(await InstallWindowsAsync(target, preview, effectivePort, arguments, cancellationToken).ConfigureAwait(false)),
            ServicePlatform.Linux => new ServiceResult(await InstallLinuxAsync(target, preview, effectivePort, command, cancellationToken).ConfigureAwait(false)),
            _ => throw new ArgumentOutOfRangeException(nameof(target), host.Platform, "unknown service platform"),
        };
    }

    /// <summary>Unload and remove <paramref name="target"/>.</summary>
    public async Task<ServiceResult> UninstallAsync(ServiceTarget target, bool preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return host.Platform switch
        {
            ServicePlatform.MacOS => new ServiceResult(await UninstallMacAsync(target, preview, cancellationToken).ConfigureAwait(false)),
            ServicePlatform.Windows => new ServiceResult(await UninstallWindowsAsync(target, preview, cancellationToken).ConfigureAwait(false)),
            ServicePlatform.Linux => new ServiceResult(await UninstallLinuxAsync(target, preview, cancellationToken).ConfigureAwait(false)),
            _ => throw new ArgumentOutOfRangeException(nameof(target), host.Platform, "unknown service platform"),
        };
    }

    /// <summary>Report whether <paramref name="target"/> is installed, running and listening.</summary>
    public async Task<ServiceStatus> StatusAsync(ServiceTarget target, bool preview, int? port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var effectivePort = target.Port(preview, port);
        return host.Platform switch
        {
            ServicePlatform.MacOS => await StatusMacAsync(target, preview, effectivePort, cancellationToken).ConfigureAwait(false),
            ServicePlatform.Windows => await StatusWindowsAsync(target, preview, effectivePort, cancellationToken).ConfigureAwait(false),
            ServicePlatform.Linux => await StatusLinuxAsync(target, preview, effectivePort, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(target), host.Platform, "unknown service platform"),
        };
    }

    /// <summary>
    /// Leave <paramref name="target"/> answering: start it only when its port is not already listening, then wait up to
    /// <paramref name="timeout"/> for it. A target without a port is installed and considered started at once.
    /// </summary>
    public async Task<bool> EnsureAsync(ServiceTarget target, int? port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var effectivePort = target.Port(preview: false, port);
        if (effectivePort is not { } listeningPort)
        {
            _ = await InstallAsync(target, preview: false, port, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (await host.Probe.ListeningAsync(listeningPort, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        _ = await InstallAsync(target, preview: false, effectivePort, cancellationToken).ConfigureAwait(false);
        var deadline = host.Clock.GetUtcNow() + timeout;
        while (host.Clock.GetUtcNow() < deadline)
        {
            await host.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
            if (await host.Probe.ListeningAsync(listeningPort, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The LaunchAgent label for <paramref name="target"/>.</summary>
    public static string Label(ServiceTarget target, bool preview)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"com.rtkelly.rig.{target.Name}{(preview ? ".preview" : string.Empty)}";
    }

    /// <summary>The LaunchAgent plist's path under <paramref name="home"/>.</summary>
    public static string PlistPath(string home, ServiceTarget target, bool preview) =>
        Path.Join(home, "Library", "LaunchAgents", Label(target, preview) + ".plist");

    /// <summary>The Task Scheduler task's name.</summary>
    public static string TaskName(ServiceTarget target, bool preview)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"{Jobs.CoordinatorTasks.Folder}\\{target.DisplayName}{(preview ? " Preview" : string.Empty)}";
    }

    /// <summary>
    /// The task's name under <see cref="Jobs.CoordinatorTasks.LegacyFolder"/>, where it was registered before the repo was
    /// renamed. Install and uninstall delete it, so an old task never keeps starting the service from the old runtime copy.
    /// </summary>
    // TODO(2027-01-31): delete with the rest of the shared-utilities -> rig migration (AppDataMigration, the legacy task names) once every machine has run `rig runtime install` and `rig jobs install`.
    public static string LegacyTaskName(ServiceTarget target, bool preview)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"{Jobs.CoordinatorTasks.LegacyFolder}\\{target.DisplayName}{(preview ? " Preview" : string.Empty)}";
    }

    /// <summary>The systemd user unit's name.</summary>
    public static string UnitName(ServiceTarget target, bool preview)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"rig-{target.Name}{(preview ? ".preview" : string.Empty)}.service";
    }

    /// <summary>The systemd user unit's path under <paramref name="home"/>.</summary>
    public static string UnitPath(string home, ServiceTarget target, bool preview) =>
        Path.Join(home, ".config", "systemd", "user", UnitName(target, preview));

    /// <summary>The LaunchAgent plist for <paramref name="target"/>.</summary>
    public static byte[] RenderPlist(ServiceHost host, ServiceTarget target, bool preview, int? port, IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(target);
        var label = Label(target, preview);
        var log = Path.Join(host.Home, "Library", "Logs", $"rig-{target.Name}{(preview ? "-preview" : string.Empty)}.log");
        var entries = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Label"] = label,
            ["ProgramArguments"] = command.ToArray(),
            ["RunAtLoad"] = true,
            ["StandardOutPath"] = log,
            ["StandardErrorPath"] = Path.Join(host.Home, "Library", "Logs", $"rig-{target.Name}{(preview ? "-preview" : string.Empty)}-err.log"),
            ["WorkingDirectory"] = host.WorkingDirectory,
        };
        if (target.LongLived)
        {
            entries["KeepAlive"] = true;
        }

        if (host.PathValue is { Length: > 0 } path)
        {
            entries["EnvironmentVariables"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["PATH"] = path };
        }

        return Plist.Render(entries);
    }

    /// <summary>The systemd user unit for <paramref name="target"/>.</summary>
    public static string RenderUnit(ServiceHost host, ServiceTarget target, bool preview, IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(target);
        var restart = target.LongLived ? "Restart=always\nRestartSec=2\n" : string.Empty;
        var description = $"rig {target.Name}{(preview ? " preview" : string.Empty)}";
        return new StringBuilder()
            .Append("[Unit]\n")
            .Append("Description=").Append(description).Append('\n')
            .Append('\n')
            .Append("[Service]\n")
            .Append("Type=simple\n")
            .Append("ExecStart=").Append(string.Join(' ', command.Select(QuoteUnit))).Append('\n')
            .Append("WorkingDirectory=").Append(host.WorkingDirectory).Append('\n')
            .Append(restart)
            .Append('\n')
            .Append("[Install]\n")
            .Append("WantedBy=default.target\n")
            .ToString();
    }

    private async Task<string> InstallMacAsync(ServiceTarget target, bool preview, int? port, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var path = PlistPath(host.Home, target, preview);
        var domain = await host.LaunchdDomain(cancellationToken).ConfigureAwait(false);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _ = await host.Proc.RunAsync(new ProcRequest(["launchctl", "bootout", domain, path]), cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(path, RenderPlist(host, target, preview, port, command), cancellationToken).ConfigureAwait(false);
        string[] bootstrap = ["launchctl", "bootstrap", domain, path];
        (await host.Proc.RunAsync(new ProcRequest(bootstrap), cancellationToken).ConfigureAwait(false)).EnsureOk(bootstrap);
        return port is { } listening
            ? $"Loaded macOS LaunchAgent [{Label(target, preview)}] running on port {listening.ToString(CultureInfo.InvariantCulture)} (PID supervised via launchd)."
            : $"Loaded macOS LaunchAgent [{Label(target, preview)}].";
    }

    private async Task<string> InstallWindowsAsync(ServiceTarget target, bool preview, int? port, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var name = TaskName(target, preview);
        var userId = host.UserId ?? throw new InvalidOperationException("Cannot register a logon task: the Windows user is unknown.");
        var (program, line) = await WindowsActionAsync(arguments, cancellationToken).ConfigureAwait(false);
        var xml = ServiceTaskXml.Render(target, preview, program, line, host.WorkingDirectory, userId);
        await new Schtasks(host.Proc, host.ScratchDirectory ?? Path.GetTempPath()).RegisterAsync(name, xml, cancellationToken).ConfigureAwait(false);
        await DeleteLegacyTaskAsync(target, preview, cancellationToken).ConfigureAwait(false);
        _ = await host.Proc.RunAsync(new ProcRequest(["schtasks", "/Run", "/TN", name]), cancellationToken).ConfigureAwait(false);
        return port is { } listening
            ? $"Registered and started Windows scheduled task [{name}] on port {listening.ToString(CultureInfo.InvariantCulture)}."
            : $"Registered and started Windows scheduled task [{name}].";
    }

    private async Task<string> InstallLinuxAsync(ServiceTarget target, bool preview, int? port, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var path = UnitPath(host.Home, target, preview);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, RenderUnit(host, target, preview, command), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var unit = UnitName(target, preview);
        await RunLinuxAsync(["systemctl", "--user", "daemon-reload"], cancellationToken).ConfigureAwait(false);
        await RunLinuxAsync(["systemctl", "--user", "enable", unit], cancellationToken).ConfigureAwait(false);
        await RunLinuxAsync(["systemctl", "--user", "reload-or-restart", unit], cancellationToken).ConfigureAwait(false);
        return port is { } listening
            ? $"Loaded systemd user unit [{unit}] running on port {listening.ToString(CultureInfo.InvariantCulture)}."
            : $"Loaded systemd user unit [{unit}].";
    }

    private async Task<string> UninstallMacAsync(ServiceTarget target, bool preview, CancellationToken cancellationToken)
    {
        var path = PlistPath(host.Home, target, preview);
        var label = Label(target, preview);
        if (!File.Exists(path))
        {
            return $"No LaunchAgent found for [{label}].";
        }

        var domain = await host.LaunchdDomain(cancellationToken).ConfigureAwait(false);
        _ = await host.Proc.RunAsync(new ProcRequest(["launchctl", "bootout", domain, path]), cancellationToken).ConfigureAwait(false);
        File.Delete(path);
        return $"Unloaded and removed macOS LaunchAgent [{label}].";
    }

    private async Task<string> UninstallWindowsAsync(ServiceTarget target, bool preview, CancellationToken cancellationToken)
    {
        var name = TaskName(target, preview);
        _ = await host.Proc.RunAsync(new ProcRequest(["schtasks", "/Delete", "/TN", name, "/F"]), cancellationToken).ConfigureAwait(false);
        await DeleteLegacyTaskAsync(target, preview, cancellationToken).ConfigureAwait(false);
        return $"Removed Windows Task Scheduler entry [{name}].";
    }

    private async Task DeleteLegacyTaskAsync(ServiceTarget target, bool preview, CancellationToken cancellationToken) =>
        _ = await host.Proc.RunAsync(new ProcRequest(["schtasks", "/Delete", "/TN", LegacyTaskName(target, preview), "/F"]), cancellationToken).ConfigureAwait(false);

    private async Task<string> UninstallLinuxAsync(ServiceTarget target, bool preview, CancellationToken cancellationToken)
    {
        var path = UnitPath(host.Home, target, preview);
        var unit = UnitName(target, preview);
        if (!File.Exists(path))
        {
            return $"No systemd unit found for [{unit}].";
        }

        _ = await host.Proc.RunAsync(new ProcRequest(["systemctl", "--user", "disable", "--now", unit]), cancellationToken).ConfigureAwait(false);
        File.Delete(path);
        _ = await host.Proc.RunAsync(new ProcRequest(["systemctl", "--user", "daemon-reload"]), cancellationToken).ConfigureAwait(false);
        return $"Unloaded and removed systemd user unit [{unit}].";
    }

    private async Task<ServiceStatus> StatusMacAsync(ServiceTarget target, bool preview, int? port, CancellationToken cancellationToken)
    {
        var installed = File.Exists(PlistPath(host.Home, target, preview));
        var domain = await host.LaunchdDomain(cancellationToken).ConfigureAwait(false);
        var running = (await host.Proc.RunAsync(new ProcRequest(["launchctl", "print", $"{domain}/{Label(target, preview)}"]), cancellationToken).ConfigureAwait(false)).Ok;
        return new ServiceStatus(installed, running, await ListeningAsync(port, cancellationToken).ConfigureAwait(false), port);
    }

    private async Task<ServiceStatus> StatusWindowsAsync(ServiceTarget target, bool preview, int? port, CancellationToken cancellationToken)
    {
        var running = (await host.Proc.RunAsync(new ProcRequest(["schtasks", "/Query", "/TN", TaskName(target, preview)]), cancellationToken).ConfigureAwait(false)).Ok;
        return new ServiceStatus(running, running, await ListeningAsync(port, cancellationToken).ConfigureAwait(false), port);
    }

    private async Task<ServiceStatus> StatusLinuxAsync(ServiceTarget target, bool preview, int? port, CancellationToken cancellationToken)
    {
        var installed = File.Exists(UnitPath(host.Home, target, preview));
        var running = (await host.Proc.RunAsync(new ProcRequest(["systemctl", "--user", "is-active", "--quiet", UnitName(target, preview)]), cancellationToken).ConfigureAwait(false)).Ok;
        return new ServiceStatus(installed, running, await ListeningAsync(port, cancellationToken).ConfigureAwait(false), port);
    }

    private async Task<bool?> ListeningAsync(int? port, CancellationToken cancellationToken) =>
        port is { } listening ? await host.Probe.ListeningAsync(listening, cancellationToken: cancellationToken).ConfigureAwait(false) : null;

    private async Task RunLinuxAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken) =>
        (await host.Proc.RunAsync(new ProcRequest(argv), cancellationToken).ConfigureAwait(false)).EnsureOk(argv);

    // pythonw running the jobs tasks' wrapper around rig, so no window appears and stopping the task stops rig; or rig
    // itself, told to free the console Windows opens for it.
    private async Task<(string Program, string Arguments)> WindowsActionAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var pythonw = host.Pythonw is { } find ? await find(cancellationToken).ConfigureAwait(false) : null;
        if (pythonw is null)
        {
            return (host.RigCommand[0], WindowsCommand([.. host.RigCommand.Skip(1), JobsInstallTarget.DetachFlag, .. arguments]));
        }

        return File.Exists(pythonw)
            ? (Path.GetFullPath(pythonw), $"-I -c \"{JobsInstallTarget.Wrapper}\" {WindowsCommand([.. host.RigCommand, .. arguments])}")
            : throw new InvalidOperationException($"pythonw is missing: {pythonw}. Pass --pythonw <path>, or --launcher direct to run without Python.");
    }

    private static string WindowsCommand(IReadOnlyList<string> command) =>
        string.Join(' ', command.Select(argument => argument.Contains(' ', StringComparison.Ordinal) || argument.Contains('"', StringComparison.Ordinal)
            ? '"' + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : argument));

    private static string QuoteUnit(string argument) =>
        argument.Contains(' ', StringComparison.Ordinal) || argument.Contains('"', StringComparison.Ordinal)
            ? '"' + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : argument;
}
