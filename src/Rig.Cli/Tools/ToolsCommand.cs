using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotMake.CommandLine;
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
[CliCommand(Name = "tools", Description = "Background service lifecycle and shared SQLite database maintenance.")]
internal sealed class ToolsCommand(ServiceHost services, DatabaseToolHost database, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
    internal ToolsCommand()
        : this(null!, null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>An unknown target or an unusable option exits with this.</summary>
    public const int UsageExit = 2;

    /// <summary>A failed install, ensure, uninstall or database operation exits with this.</summary>
    public const int FailureExit = 1;

    internal const double DefaultTimeoutSeconds = 30;

    /// <summary>The <c>tools</c> command.</summary>
    public Command Build()
    {
        var tools = DeclarativeCommand.BuildGroup(this);
        tools.Add(Targeted(new InstallCommand(this), $"Service to act on: {Targets}."));
        tools.Add(Targeted(new EnsureCommand(this), $"Service to leave answering: {Targets}."));
        tools.Add(Targeted(new UninstallCommand(this), $"Service to remove: {Targets}."));
        tools.Add(DeclarativeCommand.Build(new StatusCommand(this)));
        tools.Add(Database());
        return tools;
    }

    /// <summary>The <c>rig web install|ensure|uninstall|backup|migrate|db-status</c> aliases.</summary>
    public IReadOnlyList<Command> BuildWebAliases() =>
    [
        DeclarativeCommand.Build(new WebInstallCommand(this)),
        DeclarativeCommand.Build(new WebEnsureCommand(this)),
        DeclarativeCommand.Build(new WebUninstallCommand(this)),
        DeclarativeCommand.Build(new WebBackupCommand(this)),
        DeclarativeCommand.Build(new WebMigrateCommand(this)),
        DeclarativeCommand.Build(new WebDbStatusCommand(this)),
    ];

    private Command Database()
    {
        var db = DeclarativeCommand.BuildGroup(new DbCommand());
        db.Add(DeclarativeCommand.Build(new DbStatusCommand(this)));
        db.Add(DeclarativeCommand.Build(new DbMigrateCommand(this)));
        db.Add(DeclarativeCommand.Build(new DbBackupCommand(this)));
        return db;
    }

    private static string Targets => string.Join(", ", ServiceTarget.All.Select(t => t.Name));

    // The service list is only known at run time, so the target argument's description is set once the command is built.
    private static Command Targeted<T>(T definition, string description)
        where T : class
    {
        var command = DeclarativeCommand.Build(definition);
        command.Arguments.Single().Description = description;
        return command;
    }

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

    /// <summary>Shared by every command that takes a service target.</summary>
    private static class Text
    {
        public const string Preview = "Use the preview environment and its port.";
        public const string Port = "Port override for the service.";
        public const string Timeout = "Seconds to wait for the service to answer.";
    }

    /// <summary><c>rig tools install</c>.</summary>
    [CliCommand(Name = "install", Description = "Register and start a background service: a LaunchAgent, a logon task or a systemd user unit.")]
    internal sealed class InstallCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal InstallCommand()
            : this(null!)
        {
        }

        /// <summary>The service; described when the command is built.</summary>
        [CliArgument(Name = "target", Description = "Service to act on.")]
        public string Target { get; set; } = null!;

        /// <summary>Uses the preview environment.</summary>
        [CliOption(Name = "--preview", Description = Text.Preview)]
        public bool Preview { get; set; }

        /// <summary>Overrides the port.</summary>
        [CliOption(Name = "--port", Aliases = ["-p"], Required = false, Description = Text.Port)]
        public int? Port { get; set; }

        /// <summary>Installs the service.</summary>
        public Task<int> RunAsync(CliContext context) => tools.InstallAsync(Target, Preview, Port, context.CancellationToken);
    }

    /// <summary><c>rig tools ensure</c>.</summary>
    [CliCommand(Name = "ensure", Description = "Start a service only when its port is not answering, then wait until it is.")]
    internal sealed class EnsureCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal EnsureCommand()
            : this(null!)
        {
        }

        /// <summary>The service; described when the command is built.</summary>
        [CliArgument(Name = "target", Description = "Service to leave answering.")]
        public string Target { get; set; } = null!;

        /// <summary>Overrides the port.</summary>
        [CliOption(Name = "--port", Aliases = ["-p"], Required = false, Description = Text.Port)]
        public int? Port { get; set; }

        /// <summary>Seconds to wait.</summary>
        [CliOption(Name = "--timeout", Description = Text.Timeout)]
        public double Timeout { get; set; } = DefaultTimeoutSeconds;

        /// <summary>Ensures the service.</summary>
        public Task<int> RunAsync(CliContext context) => tools.EnsureAsync(Target, Port, Timeout, context.CancellationToken);
    }

    /// <summary><c>rig tools uninstall</c>.</summary>
    [CliCommand(Name = "uninstall", Description = "Unload and remove a service's plist, task or systemd unit.")]
    internal sealed class UninstallCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal UninstallCommand()
            : this(null!)
        {
        }

        /// <summary>The service; described when the command is built.</summary>
        [CliArgument(Name = "target", Description = "Service to remove.")]
        public string Target { get; set; } = null!;

        /// <summary>Uses the preview environment.</summary>
        [CliOption(Name = "--preview", Description = Text.Preview)]
        public bool Preview { get; set; }

        /// <summary>Removes the service.</summary>
        public Task<int> RunAsync(CliContext context) => tools.UninstallAsync(Target, Preview, context.CancellationToken);
    }

    /// <summary><c>rig tools status</c>.</summary>
    [CliCommand(Name = "status", Description = "Whether each service is installed, running and listening on its port.")]
    internal sealed class StatusCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal StatusCommand()
            : this(null!)
        {
        }

        /// <summary>The service to inspect; none is every service.</summary>
        [CliArgument(Name = "target", Required = false, Arity = CliArgumentArity.ZeroOrOne, Description = "Service to inspect. Default: every service.")]
        public string? Target { get; set; }

        /// <summary>Reports the services.</summary>
        public Task<int> RunAsync(CliContext context) => tools.StatusAsync(Target, context.CancellationToken);
    }

    /// <summary><c>rig tools db</c>.</summary>
    [CliCommand(Name = "db", Description = "The shared SQLite database: status, pending migrations, and immediate backups.")]
    internal sealed class DbCommand
    {
    }

    /// <summary><c>rig tools db status</c>.</summary>
    [CliCommand(Name = "status", Description = "Print the database path, its schema version and every applied migration.")]
    internal sealed class DbStatusCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal DbStatusCommand()
            : this(null!)
        {
        }

        /// <summary>Reports the schema.</summary>
        public Task<int> RunAsync(CliContext context) => tools.DatabaseStatusAsync(context.CancellationToken);
    }

    /// <summary><c>rig tools db migrate</c>.</summary>
    [CliCommand(Name = "migrate", Description = "Apply every pending SQLite migration.")]
    internal sealed class DbMigrateCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal DbMigrateCommand()
            : this(null!)
        {
        }

        /// <summary>Applies the migrations.</summary>
        public Task<int> RunAsync(CliContext context) => tools.MigrateAsync(context.CancellationToken);
    }

    /// <summary><c>rig tools db backup</c>.</summary>
    [CliCommand(Name = "backup", Description = "Copy the database into backups/ with an ISO-8601 timestamp.")]
    internal sealed class DbBackupCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal DbBackupCommand()
            : this(null!)
        {
        }

        /// <summary>Copies the database.</summary>
        public Task<int> RunAsync(CliContext context) => tools.BackupAsync(context.CancellationToken);
    }

    /// <summary><c>rig web install</c>.</summary>
    [CliCommand(Name = "install", Description = "Register and start the web dashboard (alias for `rig tools install web`).")]
    internal sealed class WebInstallCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal WebInstallCommand()
            : this(null!)
        {
        }

        /// <summary>Uses the preview environment.</summary>
        [CliOption(Name = "--preview", Description = Text.Preview)]
        public bool Preview { get; set; }

        /// <summary>Overrides the port.</summary>
        [CliOption(Name = "--port", Aliases = ["-p"], Required = false, Description = Text.Port)]
        public int? Port { get; set; }

        /// <summary>Installs the dashboard.</summary>
        public Task<int> RunAsync(CliContext context) => tools.InstallFixedAsync(ServiceTarget.Web, Preview, Port, context.CancellationToken);
    }

    /// <summary><c>rig web ensure</c>.</summary>
    [CliCommand(Name = "ensure", Description = "Start the web dashboard only when nothing answers, then wait (alias for `rig tools ensure web`).")]
    internal sealed class WebEnsureCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal WebEnsureCommand()
            : this(null!)
        {
        }

        /// <summary>Overrides the port.</summary>
        [CliOption(Name = "--port", Aliases = ["-p"], Required = false, Description = Text.Port)]
        public int? Port { get; set; }

        /// <summary>Seconds to wait.</summary>
        [CliOption(Name = "--timeout", Description = Text.Timeout)]
        public double Timeout { get; set; } = DefaultTimeoutSeconds;

        /// <summary>Ensures the dashboard.</summary>
        public Task<int> RunAsync(CliContext context) => tools.EnsureFixedAsync(ServiceTarget.Web, Port, Timeout, context.CancellationToken);
    }

    /// <summary><c>rig web uninstall</c>.</summary>
    [CliCommand(Name = "uninstall", Description = "Unload and remove the web dashboard service (alias for `rig tools uninstall web`).")]
    internal sealed class WebUninstallCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal WebUninstallCommand()
            : this(null!)
        {
        }

        /// <summary>Uses the preview environment.</summary>
        [CliOption(Name = "--preview", Description = Text.Preview)]
        public bool Preview { get; set; }

        /// <summary>Removes the dashboard.</summary>
        public Task<int> RunAsync(CliContext context) => tools.UninstallFixedAsync(ServiceTarget.Web, Preview, context.CancellationToken);
    }

    /// <summary><c>rig web backup</c>.</summary>
    [CliCommand(Name = "backup", Description = "Back up the shared SQLite database (alias for `rig tools db backup`).")]
    internal sealed class WebBackupCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal WebBackupCommand()
            : this(null!)
        {
        }

        /// <summary>Copies the database.</summary>
        public Task<int> RunAsync(CliContext context) => tools.BackupAsync(context.CancellationToken);
    }

    /// <summary><c>rig web migrate</c>.</summary>
    [CliCommand(Name = "migrate", Description = "Apply pending SQLite migrations (alias for `rig tools db migrate`).")]
    internal sealed class WebMigrateCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal WebMigrateCommand()
            : this(null!)
        {
        }

        /// <summary>Applies the migrations.</summary>
        public Task<int> RunAsync(CliContext context) => tools.MigrateAsync(context.CancellationToken);
    }

    /// <summary><c>rig web db-status</c>.</summary>
    [CliCommand(Name = "db-status", Description = "Report the SQLite schema version and applied migrations (alias for `rig tools db status`).")]
    internal sealed class WebDbStatusCommand(ToolsCommand tools)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal WebDbStatusCommand()
            : this(null!)
        {
        }

        /// <summary>Reports the schema.</summary>
        public Task<int> RunAsync(CliContext context) => tools.DatabaseStatusAsync(context.CancellationToken);
    }
}
