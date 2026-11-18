using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Rig.Core.Processes;
using Rig.Core.Text;

namespace Rig.Core.WorkItems;

/// <summary>
/// Claude Code sessions for the work list, read from the Claude config directory. Unlike a resume scan, which drops
/// sessions that are running now, this keeps every interactive session touched in the window and classifies it.
/// Print-mode sessions and sessions nobody typed into are dropped unless they are running now.
/// </summary>
/// <param name="home">The Claude config directory.</param>
/// <param name="ledgerDirectory">The session-end ledger's folder.</param>
/// <param name="isAlive">Whether a registered pid still runs with the registered start time.</param>
public sealed class ClaudeWorkSessions(string home, string ledgerDirectory, Func<int, string?, bool> isAlive) : IWorkSessionSource
{
    /// <summary>The Claude config directory: <c>$CLAUDE_CONFIG_DIR</c>, or <c>.claude</c> under <paramref name="userHome"/>.</summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="userHome">The user's home folder.</param>
    public static string Home(Func<string, string?> environment, string userHome)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured ? configured : Path.Join(userHome, ".claude");
    }

    /// <summary>The session-end ledger: <c>$CLAUDE_SESSION_LEDGER_DIR</c>, or <c>session-ends</c> under <paramref name="home"/>.</summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="home">The Claude config directory.</param>
    public static string LedgerDirectory(Func<string, string?> environment, string home)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment("CLAUDE_SESSION_LEDGER_DIR") is { Length: > 0 } configured ? configured : Path.Join(home, "session-ends");
    }

    /// <inheritdoc />
    public async Task<SessionScan> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var live = await SessionRegistry.ReadLiveAsync(Path.Join(home, "sessions"), isAlive, cancellationToken).ConfigureAwait(false);
        var ends = await SessionRegistry.ReadEndsAsync(ledgerDirectory, cancellationToken).ConfigureAwait(false);
        var sessions = new List<SessionFacts>();
        var read = 0;
        foreach (var path in Transcripts.Touched(Path.Join(home, "projects"), since))
        {
            SessionRecord record;
            try
            {
                record = await Transcripts.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            read++;
            var running = live.GetValueOrDefault(record.SessionId);
            if (running is null && (!record.Interactive || record.PromptCount == 0))
            {
                continue;
            }

            var state = WorkJoin.ClassifySession(
                running?.Status,
                running is not null,
                Cleared(record, ends.GetValueOrDefault(record.SessionId)),
                record.ExitedCleanly,
                record.TurnState);
            sessions.Add(new SessionFacts(
                record.SessionId,
                record.Label,
                state,
                record.LastActivity,
                string.IsNullOrEmpty(record.LastCwd) ? running?.Cwd : record.LastCwd,
                record.StartCwd,
                record.PromptCount,
                running?.Pid));
        }

        return new SessionScan(sessions, read);
    }

    // The ledger says /clear, and the transcript was not used again after that.
    private static bool Cleared(SessionRecord record, SessionEnd? end) =>
        end is { Reason: "clear" } && !(end.EndedAt is { } ended && record.LastActivity is { } active && active > ended);
}

/// <summary>A running session, from the registry.</summary>
/// <param name="Pid">The process id.</param>
/// <param name="SessionId">The session it runs.</param>
/// <param name="Cwd">Its working directory.</param>
/// <param name="Status">What Claude Code last said it was doing.</param>
internal sealed record LiveSession(int Pid, string SessionId, string? Cwd, string? Status);

/// <summary>Why and when a session ended, from the ledger.</summary>
/// <param name="Reason">The hook's reason; <c>clear</c> means the user ran <c>/clear</c>.</param>
/// <param name="EndedAt">When it ended.</param>
internal sealed record SessionEnd(string? Reason, DateTimeOffset? EndedAt);

/// <summary>
/// The two small stores beside the transcripts. The live-session registry (<c>sessions/&lt;pid&gt;.json</c>): each running
/// interactive session writes one, with its pid and the process start time; a session is live only while that pid
/// still exists with the same start time, so a recycled pid does not keep a dead session looking alive. The
/// session-end ledger (<c>session-ends/&lt;session-id&gt;.json</c>): a <c>SessionEnd</c> hook records why each session
/// ended. A crash fires no hook, so a missing record means nothing.
/// </summary>
internal static class SessionRegistry
{
    /// <summary>Registry entries whose process is still running, keyed by session id.</summary>
    public static async Task<IReadOnlyDictionary<string, LiveSession>> ReadLiveAsync(
        string directory,
        Func<int, string?, bool> isAlive,
        CancellationToken cancellationToken = default)
    {
        var live = new Dictionary<string, LiveSession>(StringComparer.Ordinal);
        await foreach (var root in ObjectsAsync(directory, cancellationToken).ConfigureAwait(false))
        {
            // The pid must be a whole number and the session id text; everything else is read if it is there.
            if (root.TryGetProperty("pid", out var pid)
                && pid.ValueKind == JsonValueKind.Number
                && pid.TryGetInt32(out var id)
                && JsonShape.Text(root, "sessionId") is { } session
                && isAlive(id, JsonShape.Text(root, "procStart")))
            {
                live[session] = new LiveSession(id, session, JsonShape.Text(root, "cwd"), JsonShape.Text(root, "status"));
            }
        }

        return live;
    }

    /// <summary>The session-end ledger, keyed by session id. Unreadable records are skipped.</summary>
    public static async Task<IReadOnlyDictionary<string, SessionEnd>> ReadEndsAsync(string directory, CancellationToken cancellationToken = default)
    {
        var ends = new Dictionary<string, SessionEnd>(StringComparer.Ordinal);
        await foreach (var root in ObjectsAsync(directory, cancellationToken).ConfigureAwait(false))
        {
            if (JsonShape.Text(root, "session_id") is { } session)
            {
                ends[session] = new SessionEnd(JsonShape.Text(root, "reason"), IsoTime.ParseIso(JsonShape.Text(root, "ended_at")));
            }
        }

        return ends;
    }

    // Every *.json under the folder that holds a JSON object, in name order; anything else is skipped.
    private static async IAsyncEnumerable<JsonElement> ObjectsAsync(
        string directory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            JsonElement? root = null;
            try
            {
                var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(text);
                root = document.RootElement.Clone();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Another program's file, half-written or not ours: it says nothing.
            }

            if (root is { ValueKind: JsonValueKind.Object } found)
            {
                yield return found;
            }
        }
    }
}

/// <summary>Whether a registered Claude Code process is still the one that registered.</summary>
public static class ProcessLiveness
{
    /// <summary>
    /// True if <paramref name="pid"/> runs and, when the registry recorded one, started at <paramref name="procStart"/>.
    /// <paramref name="procStart"/> is the process creation time as a Windows FILETIME (100ns ticks). Off Windows
    /// there is no start time to compare, so only existence is checked.
    /// </summary>
    /// <param name="pid">The registered process id.</param>
    /// <param name="procStart">The registered start time; null for old registry files.</param>
    public static bool IsAlive(int pid, string? procStart)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Exists(pid);
        }

        return WindowsStart(pid) is { } started && (procStart is null || started == procStart);
    }

    private static bool Exists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Windows-only; exercised by the Windows test job.")]
    private static string? WindowsStart(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>Which repo and branch a session's directory belongs to when it is outside every known checkout.</summary>
public static class SessionFolders
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The checkout containing <paramref name="cwd"/> and the branch checked out there; null outside git.</summary>
    /// <param name="git">Runs git.</param>
    /// <param name="cwd">A session's working directory.</param>
    public static Attribution? Resolve(Git git, string cwd)
    {
        ArgumentNullException.ThrowIfNull(git);
        if (!Directory.Exists(cwd))
        {
            return null;
        }

        var located = git.Run(cwd, ["rev-parse", "--show-toplevel", "--git-dir", "--git-common-dir"], Timeout);
        var lines = located.OutOrEmpty.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count != 3)
        {
            return null;
        }

        var branch = git.Run(cwd, ["branch", "--show-current"], Timeout).OutOrEmpty;
        return new Attribution(lines[0].Replace('/', Path.DirectorySeparatorChar), branch);
    }
}
