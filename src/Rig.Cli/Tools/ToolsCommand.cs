#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Rig.Core.Processes;
using Rig.Core.Storage;
using Rig.Core.Tools;

namespace Rig.Cli.Tools;

/// <summary>Where <c>rig tools db</c> reads and writes the shared SQLite database.</summary>
/// <param name="DbPath">The shared <c>rig.db</c>.</param>
/// <param name="BackupDir">Where <c>db backup</c> writes its copies, normally <c>&lt;app data&gt;/backups</c>.</param>
/// <param name="Clock">The clock a backup's timestamp comes from.</param>
internal sealed record DatabaseToolHost(string DbPath, string BackupDir, TimeProvider Clock);

/// <summary>
/// <c>rig tools</c>: the background service lifecycle (<c>install</c>, <c>ensure</c>, <c>uninstall</c>, <c>status</c>) for
/// the web dashboard, MCP server, desktop restore and OTel collector, plus the shared SQLite database's
/// <c>status</c>, <c>migrate</c> and <c>backup</c>. The <c>rig web</c> subcommands are aliases here.
/// </summary>
/// <param name="services">Installs and reports services on this machine's platform.</param>
/// <param name="database">Where the shared database and its backups live.</param>
/// <param name="output">Where reports go.</param>
/// <param name="error">Where failures go.</param>
internal sealed class ToolsCommand(ServiceHost services, DatabaseToolHost database, TextWriter output, TextWriter error)
{
    /// <summary>An unknown target or an unusable option exits with this.</summary>
    public const int UsageExit = 2;

    /// <summary>A failed install, ensure, uninstall or database operation exits with this.</summary>
    public const int FailureExit = 1;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The <c>tools</c> command.</summary>
    public Command Build()
    {
        var target = new Argument<string>("target")
        {
            Description = $"Service to act on: {Targets}.",
        };
        var installPreview = PreviewOption();
        var installPort = PortOption();
        var install = new Command("install", "Register and start a background service: a LaunchAgent, a logon task or a systemd user unit.") { target, installPreview, installPort };
        install.SetAction((parsed, cancellationToken) => InstallAsync(
            parsed.GetRequiredValue(target), parsed.GetValue(installPreview), parsed.GetValue(installPort), cancellationToken));

        var ensureTarget = new Argument<string>("target")
        {
            Description = $"Service to leave answering: {Targets}.",
        };
        var ensurePort = PortOption();
        var ensureTimeout = TimeoutOption();
        var ensure = new Command("ensure", "Start a service only when its port is not answering, then wait until it is.") { ensureTarget, ensurePort, ensureTimeout };
        ensure.SetAction((parsed, cancellationToken) => EnsureAsync(
            parsed.GetRequiredValue(ensureTarget), parsed.GetValue(ensurePort), parsed.GetValue(ensureTimeout), cancellationToken));

        var uninstallTarget = new Argument<string>("target")
        {
            Description = $"Service to remove: {Targets}.",
        };
        var uninstallPreview = PreviewOption();
        var uninstall = new Command("uninstall", "Unload and remove a service's plist, task or systemd unit.") { uninstallTarget, uninstallPreview };
        uninstall.SetAction((parsed, cancellationToken) => UninstallAsync(
            parsed.GetRequiredValue(uninstallTarget), parsed.GetValue(uninstallPreview), cancellationToken));

        var statusTarget = new Argument<string?>("target")
        {
            Description = "Service to inspect. Default: every service.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var status = new Command("status", "Whether each service is installed, running and listening on its port.") { statusTarget };
        status.SetAction((parsed, cancellationToken) => StatusAsync(parsed.GetValue(statusTarget), cancellationToken));

        return new Command("tools", "Background service lifecycle and shared SQLite database maintenance.")
        {
            install,
            ensure,
            uninstall,
            status,
            Database(),
        };
    }

    /// <summary>The <c>rig web install|ensure|uninstall|backup|migrate|db-status</c> aliases.</summary>
    public IReadOnlyList<Command> BuildWebAliases()
    {
        var installPreview = PreviewOption();
        var installPort = PortOption();
        var install = new Command("install", "Register and start the web dashboard (alias for `rig tools install web`).") { installPreview, installPort };
        install.SetAction((parsed, cancellationToken) => InstallFixedAsync(
            ServiceTarget.Web, parsed.GetValue(installPreview), parsed.GetValue(installPort), cancellationToken));

        var ensurePort = PortOption();
        var ensureTimeout = TimeoutOption();
        var ensure = new Command("ensure", "Start the web dashboard only when nothing answers, then wait (alias for `rig tools ensure web`).") { ensurePort, ensureTimeout };
        ensure.SetAction((parsed, cancellationToken) => EnsureFixedAsync(
            ServiceTarget.Web, parsed.GetValue(ensurePort), parsed.GetValue(ensureTimeout), cancellationToken));

        var uninstallPreview = PreviewOption();
        var uninstall = new Command("uninstall", "Unload and remove the web dashboard service (alias for `rig tools uninstall web`).") { uninstallPreview };
        uninstall.SetAction((parsed, cancellationToken) => UninstallFixedAsync(
            ServiceTarget.Web, parsed.GetValue(uninstallPreview), cancellationToken));

        var backup = new Command("backup", "Back up the shared SQLite database (alias for `rig tools db backup`).");
        backup.SetAction((_, cancellationToken) => BackupAsync(cancellationToken));
        var migrate = new Command("migrate", "Apply pending SQLite migrations (alias for `rig tools db migrate`).");
        migrate.SetAction((_, cancellationToken) => MigrateAsync(cancellationToken));
        var dbStatus = new Command("db-status", "Report the SQLite schema version and applied migrations (alias for `rig tools db status`).");
        dbStatus.SetAction((_, cancellationToken) => DatabaseStatusAsync(cancellationToken));
        return [install, ensure, uninstall, backup, migrate, dbStatus];
    }

    private Command Database()
    {
        var status = new Command("status", "Print the database path, its schema version and every applied migration.");
        status.SetAction((_, cancellationToken) => DatabaseStatusAsync(cancellationToken));
        var migrate = new Command("migrate", "Apply every pending SQLite migration.");
        migrate.SetAction((_, cancellationToken) => MigrateAsync(cancellationToken));
        var backup = new Command("backup", "Copy the database into backups/ with an ISO-8601 timestamp.");
        backup.SetAction((_, cancellationToken) => BackupAsync(cancellationToken));
        return new Command("db", "The shared SQLite database: status, pending migrations, and immediate backups.")
        {
            status,
            migrate,
            backup,
        };
    }

    private static string Targets => string.Join(", ", ServiceTarget.All.Select(t => t.Name));

    private static Option<bool> PreviewOption() =>
        new("--preview") { Description = "Use the preview environment and its port." };

    private static Option<int?> PortOption() =>
        new("--port", "-p") { Description = "Port override for the service." };

    private static Option<double> TimeoutOption() =>
        new("--timeout") { Description = "Seconds to wait for the service to answer.", DefaultValueFactory = _ => DefaultTimeout.TotalSeconds };

    private async Task<int> InstallAsync(string name, bool preview, int? port, CancellationToken cancellationToken)
    {
        if (Target(name) is not { } target)
        {
            return Unknown(name);
        }

        return await InstallFixedAsync(target, preview, port, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> EnsureAsync(string name, int? port, double timeout, CancellationToken cancellationToken)
    {
        if (Target(name) is not { } target)
        {
            return Unknown(name);
        }

        return await EnsureFixedAsync(target, port, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> UninstallAsync(string name, bool preview, CancellationToken cancellationToken)
    {
        if (Target(name) is not { } target)
        {
            return Unknown(name);
        }

        return await UninstallFixedAsync(target, preview, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> InstallFixedAsync(ServiceTarget target, bool preview, int? port, CancellationToken cancellationToken)
    {
        try
        {
            var result = await new ServiceManager(services).InstallAsync(target, preview, port, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(result.Message).ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is ProcException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await error.WriteLineAsync($"rig tools install {target.Name}: {e.Message}").ConfigureAwait(false);
            return FailureExit;
        }
    }

    private async Task<int> EnsureFixedAsync(ServiceTarget target, int? port, double timeout, CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromSeconds(timeout);
        try
        {
            var answering = await new ServiceManager(services).EnsureAsync(target, port, wait, cancellationToken).ConfigureAwait(false);
            var effectivePort = target.Port(preview: false, port);
            if (!answering)
            {
                await error.WriteLineAsync(
                    $"{target.DisplayName} did not answer on port {effectivePort?.ToString(CultureInfo.InvariantCulture) ?? "?"} within {timeout.ToString("g", CultureInfo.InvariantCulture)}s.")
                    .ConfigureAwait(false);
                return FailureExit;
            }

            await output.WriteLineAsync(effectivePort is { } listening
                ? $"{target.DisplayName} answering on http://127.0.0.1:{listening.ToString(CultureInfo.InvariantCulture)}"
                : $"{target.DisplayName} started.").ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is ProcException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await error.WriteLineAsync($"rig tools ensure {target.Name}: {e.Message}").ConfigureAwait(false);
            return FailureExit;
        }
    }

    private async Task<int> UninstallFixedAsync(ServiceTarget target, bool preview, CancellationToken cancellationToken)
    {
        try
        {
            var result = await new ServiceManager(services).UninstallAsync(target, preview, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(result.Message).ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is ProcException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await error.WriteLineAsync($"rig tools uninstall {target.Name}: {e.Message}").ConfigureAwait(false);
            return FailureExit;
        }
    }

    private async Task<int> StatusAsync(string? name, CancellationToken cancellationToken)
    {
        ServiceTarget[] targets;
        if (name is null)
        {
            targets = [.. ServiceTarget.All];
        }
        else if (Target(name) is { } target)
        {
            targets = [target];
        }
        else
        {
            return Unknown(name);
        }

        var manager = new ServiceManager(services);
        foreach (var target in targets)
        {
            try
            {
                var status = await manager.StatusAsync(target, preview: false, port: null, cancellationToken).ConfigureAwait(false);
                var installed = status.Installed ? "installed" : "not installed";
                var running = status.Running ? "running" : "stopped";
                var listening = status.Listening is { } open
                    ? open
                        ? $", listening on {status.Port?.ToString(CultureInfo.InvariantCulture)}"
                        : $", not listening on {status.Port?.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty;
                await output.WriteLineAsync($"{target.Name}: {installed}, {running}{listening}").ConfigureAwait(false);
            }
            catch (Exception e) when (e is ProcException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await error.WriteLineAsync($"rig tools status {target.Name}: {e.Message}").ConfigureAwait(false);
                return FailureExit;
            }
        }

        return 0;
    }

    private async Task<int> DatabaseStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var state = RigDatabase.Inspect(database.DbPath);
            await output.WriteLineAsync($"Database Path: {database.DbPath}").ConfigureAwait(false);
            await output.WriteLineAsync($"Current Schema Version: {state.Version.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            if (state.Applied.Count == 0)
            {
                await output.WriteLineAsync("No migrations have been recorded yet.").ConfigureAwait(false);
                return 0;
            }

            await output.WriteLineAsync("Applied Schema Migrations").ConfigureAwait(false);
            var rows = state.Applied.Select(m => (Version: m.Version.ToString(CultureInfo.InvariantCulture), m.Name, m.AppliedAt)).ToArray();
            var versionWidth = Math.Max("Version".Length, rows.Max(r => r.Version.Length));
            var nameWidth = Math.Max("Name".Length, rows.Max(r => r.Name.Length));
            await output.WriteLineAsync($"{Pad("Version", versionWidth)}  {Pad("Name", nameWidth)}  Applied At").ConfigureAwait(false);
            foreach (var (version, migrationName, appliedAt) in rows)
            {
                await output.WriteLineAsync($"{Pad(version, versionWidth)}  {Pad(migrationName, nameWidth)}  {appliedAt}").ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException)
        {
            await error.WriteLineAsync($"rig tools db status: {e.Message}").ConfigureAwait(false);
            return FailureExit;
        }
    }

    private async Task<int> MigrateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var applied = RigDatabase.Migrate(database.DbPath, database.Clock);
            await output.WriteLineAsync($"Migrations applied: {applied.ToString(CultureInfo.InvariantCulture)} on {database.DbPath}").ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
        {
            await error.WriteLineAsync($"rig tools db migrate: {e.Message}").ConfigureAwait(false);
            return FailureExit;
        }
    }

    private async Task<int> BackupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = RigDatabase.Backup(database.DbPath, database.BackupDir, database.Clock);
            await output.WriteLineAsync($"Database backup created: {path}").ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            await error.WriteLineAsync($"rig tools db backup: {e.Message}").ConfigureAwait(false);
            return FailureExit;
        }
    }

    private static ServiceTarget? Target(string name) => ServiceTarget.Find(name);

    private int Unknown(string name)
    {
        error.WriteLine($"Unknown target '{name}'. Choose {Targets}.");
        return UsageExit;
    }

    private static string Pad(string value, int width) => value.PadRight(width, ' ');
}
