using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rig.Core.GitHub;
using Rig.Core.ReviewQueue;

namespace Rig.Cli.ReviewQueue;

/// <summary>Explicit local paths for triage's database, transient projection and managed registry.</summary>
/// <param name="Database">Shared rig SQLite file.</param>
/// <param name="Queue">Transient queue Markdown, outside the vault.</param>
/// <param name="Registry">Default managed-projects file.</param>
internal sealed record TriageHost(string Database, string Queue, string Registry);

/// <summary><c>rig triage</c>: refresh the persistent queue or verify its Markdown without changing it.</summary>
internal sealed class TriageCommand(TriageHost host, IQueueSource source, TextWriter output, TextWriter error)
{
    /// <summary>The complete triage CLI, with no Python fallback.</summary>
    public Command Build()
    {
        var db = new Option<string?>("--db") { Description = "Rig SQLite database override." };
        var queue = new Option<string?>("--output") { Description = "Transient queue Markdown override." };
        var registry = new Option<string?>("--config") { Description = "Managed project registry override." };
        var refresh = new Command("refresh", "Refresh managed repositories and update the Markdown projection.") { db, queue, registry };
        refresh.SetAction((parsed, cancellationToken) => RefreshAsync(parsed.GetValue(db) ?? host.Database,
            parsed.GetValue(queue) ?? host.Queue, parsed.GetValue(registry) ?? host.Registry, cancellationToken));

        var path = new Argument<string?>("path") { Arity = ArgumentArity.ZeroOrOne, Description = "Queue Markdown; defaults to rig's local transient queue." };
        var verify = new Command("verify", "Check local Markdown structure and deterministic order. Never update any file or DB.") { path };
        verify.SetAction(parsed =>
        {
            var target = parsed.GetValue(path);
            // System.CommandLine accepts an unmatched option as a positional string. Typer required -- for these.
            if (target?.StartsWith('-') == true && !parsed.Tokens.Any(token => token.Type == TokenType.DoubleDash))
            {
                error.Write($"rig: Unrecognized command or argument '{target}'.\nRun 'rig --help' for the commands.\n");
                return 2;
            }

            return Verify(target ?? host.Queue);
        });
        return new Command("triage", "Persistent PR and issue triage queue.") { refresh, verify };
    }

    private async Task<int> RefreshAsync(string db, string queue, string registry, CancellationToken cancellationToken)
    {
        try
        {
            using var service = new QueueService(new QueueStore(db), source, queue);
            var entries = await service.RefreshAsync(registry, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Tracked {entries.Count} items; queue: {queue}").ConfigureAwait(false);
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or JsonException or SqliteException or GhException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            await error.WriteLineAsync($"Triage refresh failed: {e.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private int Verify(string path)
    {
        try
        {
            QueueMarkdown.Verify(QueueProjection.Read(path));
            output.Write($"Valid triage queue: {path}\n");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            error.Write($"Invalid triage queue: {e.Message}\n");
            return 1;
        }
    }
}
