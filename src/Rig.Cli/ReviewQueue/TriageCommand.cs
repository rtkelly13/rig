using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using DotMake.CommandLine;
using Microsoft.Data.Sqlite;
using Rig.Cli.Commands;
using Rig.Core;
using Rig.Core.GitHub;
using Rig.Core.ReviewQueue;

namespace Rig.Cli.ReviewQueue;

/// <summary>Explicit local paths for triage's database, transient projection and managed registry.</summary>
/// <param name="Database">Shared rig SQLite file.</param>
/// <param name="Queue">Transient queue Markdown, outside the vault.</param>
/// <param name="Registry">Default managed-projects file.</param>
internal sealed record TriageHost(string Database, string Queue, string Registry);

/// <summary><c>rig triage</c>: refresh the persistent queue or verify its Markdown without changing it.</summary>
/// <param name="host">The database, projection and registry triage reads.</param>
/// <param name="source">Where the tracked items come from.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where the failures go.</param>
[CliCommand(Name = "triage", Description = "Persistent PR and issue triage queue.")]
internal sealed class TriageCommand(TriageHost host, IQueueSource source, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal TriageCommand()
        : this(null!, null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The complete triage CLI.</summary>
    public Command Build()
    {
        var triage = DeclarativeCommand.Build(this);
        triage.Add(DeclarativeCommand.Build(new RefreshCommand(host, source, output, error)));
        triage.Add(DeclarativeCommand.Build(new VerifyCommand(host, output, error)));
        return triage;
    }

    /// <summary>A group named without one of its verbs is the parser's error.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(error);

    /// <summary><c>rig triage refresh</c>: fetch tracked items and rewrite the queue.</summary>
    [CliCommand(Name = "refresh", Description = "Refresh managed repositories and update the Markdown projection.")]
    internal sealed class RefreshCommand(TriageHost host, IQueueSource source, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal RefreshCommand()
            : this(null!, null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Rig SQLite database override.</summary>
        [CliOption(Name = "--db", Required = false, Description = "Rig SQLite database override.")]
        public string? Database { get; set; }

        /// <summary>Transient queue Markdown override.</summary>
        [CliOption(Name = "--output", Required = false, Description = "Transient queue Markdown override.")]
        public string? Queue { get; set; }

        /// <summary>Managed project registry override.</summary>
        [CliOption(Name = "--config", Required = false, Description = "Managed project registry override.")]
        public string? Registry { get; set; }

        /// <summary>Refreshes the queue and says how many items it tracked.</summary>
        public async Task<int> RunAsync(CliContext context)
        {
            var cancellationToken = context.CancellationToken;
            var db = Database ?? host.Database;
            var queue = Queue ?? host.Queue;
            try
            {
                using var service = new QueueService(new QueueStore(db), source, queue);
                var entries = await service.RefreshAsync(Registry ?? host.Registry, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Tracked {entries.Count} items; queue: {queue}").ConfigureAwait(false);
                return (int)ExitCode.Clean;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or JsonException or SqliteException or GhException
                || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                await error.WriteLineAsync($"Triage refresh failed: {e.Message}").ConfigureAwait(false);
                return (int)ExitCode.Drift;
            }
        }
    }

    /// <summary><c>rig triage verify</c>: the Markdown as it stands, never written.</summary>
    [CliCommand(Name = "verify", Description = "Check local Markdown structure and deterministic order. Never update any file or DB.")]
    internal sealed class VerifyCommand(TriageHost host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal VerifyCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Queue Markdown; defaults to rig's local transient queue.</summary>
        [CliArgument(Name = "path", Required = false, Arity = CliArgumentArity.ZeroOrOne, Description = "Queue Markdown; defaults to rig's local transient queue.")]
        public string? Path { get; set; }

        /// <summary>Verifies the queue and says whether it is valid.</summary>
        public int Run(CliContext context)
        {
            // System.CommandLine accepts an unmatched option as a positional string.
            if (Path?.StartsWith('-', StringComparison.Ordinal) == true && !context.Result.ParseResult.Tokens.Any(token => token.Type == TokenType.DoubleDash))
            {
                error.Write($"rig: Unrecognized command or argument '{Path}'.\nRun 'rig --help' for the commands.\n");
                return (int)ExitCode.Error;
            }

            return Verify(Path ?? host.Queue);
        }

        private int Verify(string path)
        {
            try
            {
                QueueMarkdown.Verify(QueueProjection.Read(path));
                output.Write($"Valid triage queue: {path}\n");
                return (int)ExitCode.Clean;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
            {
                error.Write($"Invalid triage queue: {e.Message}\n");
                return (int)ExitCode.Drift;
            }
        }
    }
}
