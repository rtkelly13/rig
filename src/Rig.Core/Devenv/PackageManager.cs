using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Devenv;

/// <summary>A package the manager can upgrade.</summary>
/// <param name="Id">The package id, as the catalog names it.</param>
/// <param name="Installed">The installed version.</param>
/// <param name="Available">The version an upgrade would install.</param>
public sealed record OutdatedPackage(string Id, string Installed, string Available);

/// <summary>
/// The platform's package manager: winget on Windows, Homebrew elsewhere. Every call is a process; a failure is
/// <see langword="false"/>, never an exception.
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

    /// <summary>
    /// Which of <paramref name="ids"/> have an upgrade, asked once and filtered here; null when the manager could not
    /// say, so a failed lookup never reads as up to date.
    /// </summary>
    public abstract Task<IReadOnlyList<OutdatedPackage>?> OutdatedAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken);

    /// <summary>Runs one command, attached to the console unless <paramref name="quiet"/>, with a five-minute limit.</summary>
    protected Task<Ran> RunAsync(IReadOnlyList<string> argv, bool quiet, CancellationToken cancellationToken) =>
        proc.RunAsync(new ProcRequest(argv) { Timeout = Limit, Attached = !quiet }, cancellationToken);
}

/// <summary>Homebrew: what is installed comes from one <c>brew list</c>, read once.</summary>
/// <param name="proc">Runs brew.</param>
public sealed class Homebrew(IProc proc) : PackageManager(proc)
{
    private static readonly string[] Sections = ["formulae", "casks"];
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

        return _installed.Contains(Short(id));
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<OutdatedPackage>?> OutdatedAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var ran = await RunAsync(["brew", "outdated", "--json=v2"], quiet: true, cancellationToken).ConfigureAwait(false);
        return ran.Ok ? ParseOutdated(ran.Out, ids) : null;
    }

    /// <summary>
    /// The formulae and casks in <c>brew outdated --json=v2</c> output that <paramref name="ids"/> name, by short name;
    /// null when it is not that shape.
    /// </summary>
    public static IReadOnlyList<OutdatedPackage>? ParseOutdated(string json, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.GroupBy(Short, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return null;
            }

            return [.. Sections
                .SelectMany(section => root[section] is JsonArray entries ? entries.OfType<JsonObject>() : [])
                .Select(entry => (Name: (string?)entry["name"], Installed: entry["installed_versions"] is JsonArray versions ? (string?)versions.LastOrDefault() : null, Available: (string?)entry["current_version"]))
                .Where(entry => entry.Name is not null && wanted.ContainsKey(Short(entry.Name)))
                .Select(entry => new OutdatedPackage(wanted[Short(entry.Name!)], entry.Installed ?? "?", entry.Available ?? "?"))];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // A tapped formula (owner/tap/name) is listed by its last segment.
    private static string Short(string id) => id[(id.LastIndexOf('/', StringComparison.Ordinal) + 1)..].ToLowerInvariant();

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

    /// <inheritdoc />
    public override async Task<IReadOnlyList<OutdatedPackage>?> OutdatedAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var ran = await RunAsync(["winget", "upgrade", "--accept-source-agreements", "--disable-interactivity"], quiet: true, cancellationToken).ConfigureAwait(false);

        // winget can exit non-zero when nothing is upgradable; its table is still the answer when it printed one.
        return ran.Ok || HasTable(ran.Out) ? ParseUpgrades(ran.Out, ids) : null;
    }

    /// <summary>
    /// The rows of <c>winget upgrade</c>'s table whose Id is one of <paramref name="ids"/>. A row is found by its Id token,
    /// not by column offsets, because names can be wide; the Version and Available follow it, and an unknown version
    /// printed as <c>&lt; 1.2</c> is kept whole. Spinner frames before a carriage return are dropped.
    /// </summary>
    public static IReadOnlyList<OutdatedPackage> ParseUpgrades(string table, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. table
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line[(line.LastIndexOf('\r', StringComparison.Ordinal) + 1)..])
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Select(tokens => (Tokens: tokens, At: Array.FindIndex(tokens, token => wanted.Contains(token))))
            .Where(row => row.At >= 0)
            .Select(row => Versions(row.Tokens[(row.At + 1)..]) is { } versions ? new OutdatedPackage(ids.First(id => string.Equals(id, row.Tokens[row.At], StringComparison.OrdinalIgnoreCase)), versions.Installed, versions.Available) : null)
            .OfType<OutdatedPackage>()];
    }

    private static bool HasTable(string text) => text.Split('\n').Any(line => line.TrimStart().StartsWith("---", StringComparison.Ordinal));

    // Version then Available; a comparison sign ("<" or ">") is joined to the version it qualifies.
    private static (string Installed, string Available)? Versions(string[] rest)
    {
        var versions = new Queue<string>(rest);
        string? Next() => versions.TryDequeue(out var token) ? token is "<" or ">" && versions.TryDequeue(out var version) ? $"{token} {version}" : token : null;
        return Next() is { } installed && Next() is { } available ? (installed, available) : null;
    }

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

    /// <summary>
    /// Which of <paramref name="tools"/> the package manager can upgrade, each with its versions; a tool with no package
    /// on this manager is never listed. Null when there is no package manager or it could not say.
    /// </summary>
    public async Task<IReadOnlyList<(DevTool Tool, OutdatedPackage Package)>?> OutdatedAsync(IReadOnlyList<DevTool> tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (manager is null)
        {
            return null;
        }

        var packaged = tools.Select(tool => (Tool: tool, Id: manager.IdOf(tool.Install))).Where(entry => entry.Id is not null).ToList();
        var outdated = await manager.OutdatedAsync([.. packaged.Select(entry => entry.Id!).Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
        if (outdated is null)
        {
            return null;
        }

        var byId = outdated.GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return [.. packaged.Where(entry => byId.ContainsKey(entry.Id!)).Select(entry => (entry.Tool, byId[entry.Id!]))];
    }

    /// <summary>Installs <paramref name="tool"/>; false when there is no package manager or package for it, or the install fails.</summary>
    public async Task<bool> InstallAsync(DevTool tool, bool quiet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return manager?.IdOf(tool.Install) is { } id && await manager.InstallAsync(id, quiet, cancellationToken).ConfigureAwait(false);
    }
}
