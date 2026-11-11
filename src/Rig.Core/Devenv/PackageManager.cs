using Rig.Core.Processes;

namespace Rig.Core.Devenv;

/// <summary>
/// The platform's package manager: winget on Windows, Homebrew elsewhere. Every call is a process; a failure is
/// <see langword="false"/>, never an exception. Ported from devenv's <c>providers</c>.
/// </summary>
/// <param name="proc">Runs the package manager.</param>
public abstract class PackageManager(IProc proc)
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>winget on Windows or Homebrew elsewhere, when it is on the PATH; otherwise null.</summary>
    public static PackageManager? For(bool windows, Func<string, string?> find, IProc proc)
    {
        ArgumentNullException.ThrowIfNull(find);
        return windows
            ? find("winget") is null ? null : new Winget(proc)
            : find("brew") is null ? null : new Homebrew(proc);
    }

    /// <summary>The program's name.</summary>
    public abstract string Name { get; }

    /// <summary>The package id this manager installs <paramref name="install"/> by, or null when it has none.</summary>
    public abstract string? IdOf(ToolInstall install);

    /// <summary>Whether the package is installed.</summary>
    public abstract Task<bool> IsInstalledAsync(string id, CancellationToken cancellationToken);

    /// <summary>Installs the package. Quiet captures its output and, for winget, adds the non-interactive flags.</summary>
    public abstract Task<bool> InstallAsync(string id, bool quiet, CancellationToken cancellationToken);

    /// <summary>Upgrades every package it owns.</summary>
    public abstract Task<bool> UpgradeAllAsync(bool quiet, CancellationToken cancellationToken);

    /// <summary>Runs one command, attached to the console unless <paramref name="quiet"/>, with a five-minute limit.</summary>
    protected Task<Ran> RunAsync(IReadOnlyList<string> argv, bool quiet, CancellationToken cancellationToken) =>
        proc.RunAsync(new ProcRequest(argv) { Timeout = Limit, Attached = !quiet }, cancellationToken);
}

/// <summary>Homebrew: what is installed comes from one <c>brew list</c>, read once.</summary>
/// <param name="proc">Runs brew.</param>
public sealed class Homebrew(IProc proc) : PackageManager(proc)
{
    private HashSet<string>? _installed;

    /// <inheritdoc />
    public override string Name => "brew";

    /// <inheritdoc />
    public override string? IdOf(ToolInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return install.Brew;
    }

    /// <inheritdoc />
    public override async Task<bool> IsInstalledAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_installed is null)
        {
            var listed = await RunAsync(["brew", "list"], quiet: true, cancellationToken).ConfigureAwait(false);
            _installed = new HashSet<string>((listed.Ok ? listed.Out : string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);
        }

        // A tapped formula (owner/tap/name) is listed by its last segment.
        return _installed.Contains(id[(id.LastIndexOf('/') + 1)..].ToLowerInvariant());
    }

    /// <inheritdoc />
    public override async Task<bool> InstallAsync(string id, bool quiet, CancellationToken cancellationToken) =>
        (await RunAsync(["brew", "install", id], quiet, cancellationToken).ConfigureAwait(false)).Ok;

    /// <inheritdoc />
    public override async Task<bool> UpgradeAllAsync(bool quiet, CancellationToken cancellationToken) =>
        (await RunAsync(["brew", "upgrade"], quiet, cancellationToken).ConfigureAwait(false)).Ok;
}

/// <summary>winget, asked package by package.</summary>
/// <param name="proc">Runs winget.</param>
public sealed class Winget(IProc proc) : PackageManager(proc)
{
    private static readonly string[] Agreements = ["--accept-package-agreements", "--accept-source-agreements"];
    private static readonly string[] Unattended = ["--silent", "--disable-interactivity"];

    /// <inheritdoc />
    public override string Name => "winget";

    /// <inheritdoc />
    public override string? IdOf(ToolInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return install.Winget;
    }

    /// <inheritdoc />
    public override async Task<bool> IsInstalledAsync(string id, CancellationToken cancellationToken)
    {
        var listed = await RunAsync(["winget", "list", "--id", id, "--exact"], quiet: true, cancellationToken).ConfigureAwait(false);
        return listed.Ok && listed.Out.Contains(id, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override async Task<bool> InstallAsync(string id, bool quiet, CancellationToken cancellationToken) =>
        (await RunAsync(Flags(["winget", "install", "--id", id, "--exact"], quiet), quiet, cancellationToken).ConfigureAwait(false)).Ok;

    /// <inheritdoc />
    public override async Task<bool> UpgradeAllAsync(bool quiet, CancellationToken cancellationToken) =>
        (await RunAsync(Flags(["winget", "upgrade", "--all"], quiet), quiet, cancellationToken).ConfigureAwait(false)).Ok;

    private static string[] Flags(string[] command, bool quiet) => [.. command, .. Agreements, .. quiet ? Unattended : []];
}

/// <summary>Checks and installs catalog tools: a tool is installed when its program is on the PATH or its package is.</summary>
/// <param name="manager">The platform's package manager, or null when there is none.</param>
/// <param name="find">Finds a program on the PATH, or null.</param>
public sealed class DevTools(PackageManager? manager, Func<string, string?> find)
{
    /// <summary>The package manager, or null.</summary>
    public PackageManager? Manager => manager;

    /// <summary>Whether <paramref name="tool"/> is installed.</summary>
    public async Task<bool> IsInstalledAsync(DevTool tool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(find);
        if (tool.Install.CheckCmd is { } program && find(program) is not null)
        {
            return true;
        }

        return manager?.IdOf(tool.Install) is { } id && await manager.IsInstalledAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs <paramref name="tool"/>; false when there is no package manager or package for it, or the install fails.</summary>
    public async Task<bool> InstallAsync(DevTool tool, bool quiet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return manager?.IdOf(tool.Install) is { } id && await manager.InstallAsync(id, quiet, cancellationToken).ConfigureAwait(false);
    }
}
