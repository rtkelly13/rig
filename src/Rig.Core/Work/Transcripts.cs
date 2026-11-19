using System.Text;
using System.Text.Json;
using Rig.Core.Text;

namespace Rig.Core.WorkItems;

/// <summary>What one Claude Code transcript says about its session.</summary>
/// <param name="SessionId">The transcript's file name without its extension.</param>
/// <param name="StartCwd">The first working directory recorded: where the session started.</param>
/// <param name="LastCwd">The last working directory recorded: where the work was when it stopped.</param>
/// <param name="Title">The custom title, else the generated title, else the summary; null when it has none.</param>
/// <param name="FirstPrompt">The first prompt the user typed.</param>
/// <param name="PromptCount">How many prompts the user typed.</param>
/// <param name="LastActivity">The newest readable timestamp on an entry that moved the conversation on.</param>
/// <param name="ExitedCleanly">A <c>cost-state</c> entry follows the last activity; a crash leaves none.</param>
/// <param name="Interactive">Started from the terminal rather than print mode or an SDK.</param>
/// <param name="TurnState">How the last turn ended: one of the <see cref="Transcripts"/> turn states.</param>
internal sealed record SessionRecord(
    string SessionId,
    string? StartCwd,
    string? LastCwd,
    string? Title,
    string? FirstPrompt,
    int PromptCount,
    DateTimeOffset? LastActivity,
    bool ExitedCleanly,
    bool Interactive,
    string TurnState)
{
    /// <summary>The title, else the start of the first prompt, else the start of the session id.</summary>
    public string Label
    {
        get
        {
            if (!string.IsNullOrEmpty(Title))
            {
                return Title;
            }

            var prompt = WorkText.Clip((FirstPrompt ?? string.Empty).Trim(), 60);
            return prompt.Length > 0 ? prompt : WorkText.Clip(SessionId, 8);
        }
    }
}

/// <summary>
/// Reads Claude Code transcripts (<c>projects/&lt;project&gt;/&lt;session-id&gt;.jsonl</c>). Subagent transcripts live
/// one level deeper and are never read. A clean exit appends a <c>cost-state</c> entry after the last message; a
/// crash, an OOM kill or a killed terminal leaves none. How the last turn ended says whether the work finished: a
/// reply in plain text means the session was waiting on the user; a tool call or a tool result with nothing after
/// it means it died mid-task. The format is Claude Code's, so every field is optional and unknown fields are ignored; a field rig reads
/// with the wrong type, or a line that is not JSON, skips that line and is reported.
/// </summary>
internal static class Transcripts
{
    /// <summary>The last entry is assistant text; the session was waiting for the user.</summary>
    public const string Answered = "answered";

    /// <summary>A tool call or its result is last; the session died in the middle of work.</summary>
    public const string MidTool = "mid-tool";

    /// <summary>A typed prompt is last; the session died before replying.</summary>
    public const string Unanswered = "unanswered";

    private const string InteractiveEntrypoint = "cli";

    // Tool inputs nest deeply; a line is skipped only when it is not JSON, not because it is deep.
    private static readonly JsonDocumentOptions DeepDocuments = new() { MaxDepth = 1024 };

    /// <summary>The top-level transcripts under <paramref name="projects"/> modified at or after <paramref name="since"/>, in a stable order.</summary>
    public static IReadOnlyList<string> Touched(string projects, DateTimeOffset since)
    {
        if (!Directory.Exists(projects))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateDirectories(projects)
                .Order(StringComparer.Ordinal)
                .SelectMany(In)
                .Where(path => File.GetLastWriteTimeUtc(path) >= since.UtcDateTime),
        ];
    }

    // A project folder that cannot be read, or that went away since it was listed, has no transcripts to offer;
    // it must not cost the report every other project's sessions.
    private static IReadOnlyList<string> In(string project)
    {
        try
        {
            return [.. Directory.EnumerateFiles(project, "*.jsonl").Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Summarises the transcript at <paramref name="path"/>. A line that is not JSON, or holds a field of the wrong type, is skipped,
    /// never fatal; <paramref name="problem"/> hears <c>path:line: $.json.path: expected X, found Y</c> for each.
    /// </summary>
    public static async Task<SessionRecord> ReadAsync(string path, Action<string>? problem = null, CancellationToken cancellationToken = default)
    {
        var summary = new Summary();
        var number = 0;

        // Claude appends to a live transcript while rig reads it; sharing writes and deletes keeps Windows from refusing the open.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, options);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            summary.Add(line, $"{path}:{++number}", problem);
        }

        return summary.Finish(Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Summarises a transcript's <paramref name="lines"/>; <paramref name="problem"/> hears each line skipped, as <see cref="ReadAsync"/> words it.</summary>
    public static SessionRecord Summarise(string sessionId, IEnumerable<string> lines, Action<string>? problem = null)
    {
        var summary = new Summary();
        var number = 0;
        foreach (var line in lines)
        {
            summary.Add(line, $"{sessionId}:{++number}", problem);
        }

        return summary.Finish(sessionId);
    }

    // A flag is a JSON boolean or absent: 1, "yes" and [] are not booleans.
    private static bool Flag(JsonElement parent, string name, string path) =>
        !parent.TryGetProperty(name, out var value)
            ? false
            : value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw JsonProblem.Expected(JsonProblem.Child(path, name), "a boolean", JsonProblem.Describe(value)),
            };

    // A string field, or absent. Null is a wrong type here, as it is for every strict string.
    private static string? Text(JsonElement parent, string name) => JsonProblem.OptionalString(parent, name, "$");

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    // The entry a line holds, or null for a blank line. A line that is not a JSON object, or holds a field rig reads with the wrong
    // type, is a JsonInputException naming the source.
    private static Entry? Parse(string line, string source)
    {
        if (line.Trim().Length == 0)
        {
            return null;
        }

        return JsonProblem.Run(source, () =>
        {
            using var document = JsonDocument.Parse(line, DeepDocuments);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw JsonProblem.Expected("$", "an object", JsonProblem.Describe(root));
            }

            string? text = null;
            IReadOnlyList<string?> blocks = [];
            if (root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString();
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    blocks = [.. content.EnumerateArray().Where(block => block.ValueKind == JsonValueKind.Object).Select(block => Text(block, "type"))];
                }
            }

            return new Entry(
                Text(root, "type"),
                Text(root, "cwd"),
                Text(root, "entrypoint"),
                Text(root, "timestamp"),
                text,
                blocks,
                Flag(root, "isCompactSummary", "$"),
                Flag(root, "isMeta", "$"),
                Text(root, "customTitle"),
                Text(root, "aiTitle"),
                Text(root, "summary"));
        });
    }

    // One transcript line: the content as typed text, or the types of its blocks.
    private sealed record Entry(
        string? Type,
        string? Cwd,
        string? Entrypoint,
        string? Timestamp,
        string? Content,
        IReadOnlyList<string?> BlockTypes,
        bool CompactSummary,
        bool Meta,
        string? CustomTitle,
        string? AiTitle,
        string? Summary)
    {
        // The text of a prompt the user typed; null for tool results, reminders and compaction summaries.
        public string? TypedPrompt =>
            CompactSummary || Meta || Content is null || Content.StartsWith('<', StringComparison.Ordinal) ? null : Content;

        // How the conversation ended, judged from this, its last user or assistant entry.
        public string TurnState
        {
            get
            {
                if (CompactSummary)
                {
                    return Unanswered;
                }

                if (Type == "assistant")
                {
                    return BlockTypes.Contains("tool_use") ? MidTool : Answered;
                }

                if (BlockTypes.Contains("tool_result"))
                {
                    return MidTool;
                }

                return TypedPrompt is null ? Answered : Unanswered;
            }
        }
    }

    private sealed class Summary
    {
        private int _line;
        private string? _startCwd;
        private string? _lastCwd;
        private string? _customTitle;
        private string? _aiTitle;
        private string? _summary;
        private string? _firstPrompt;
        private int _promptCount;
        private DateTimeOffset? _lastActivity;
        private int _lastActivityLine = -1;
        private int _lastCostStateLine = -1;
        private string? _entrypoint;
        private Entry? _lastTurn;

        public void Add(string line, string source, Action<string>? problem)
        {
            var number = _line++;
            Entry? parsed;
            try
            {
                parsed = Parse(line, source);
            }
            catch (JsonInputException e)
            {
                problem?.Invoke(e.Message);
                return;
            }

            if (parsed is not { } entry)
            {
                return;
            }

            if (entry.Cwd is not null)
            {
                _lastCwd = entry.Cwd;
                _startCwd = NonEmpty(_startCwd) ?? _lastCwd;
            }

            _entrypoint ??= entry.Entrypoint;
            switch (entry.Type)
            {
                case "cost-state":
                    _lastCostStateLine = number;
                    break;
                case "custom-title":
                    _customTitle = NonEmpty(entry.CustomTitle) ?? _customTitle;
                    break;
                case "ai-title":
                    _aiTitle = NonEmpty(entry.AiTitle) ?? _aiTitle;
                    break;
                case "summary":
                    _summary = NonEmpty(entry.Summary) ?? _summary;
                    break;

                // The entries that mean the conversation moved on. Bookkeeping entries (titles, modes, last-prompt)
                // are written around a clean exit too, so they do not count.
                case "user" or "assistant" or "system" or "attachment":
                    Activity(entry, number);
                    break;
                default:
                    break;
            }
        }

        public SessionRecord Finish(string sessionId) =>
            new(
                sessionId,
                _startCwd,
                _lastCwd,
                _customTitle ?? _aiTitle ?? _summary,
                _firstPrompt,
                _promptCount,
                _lastActivity,
                _lastCostStateLine > _lastActivityLine,
                _entrypoint is null or InteractiveEntrypoint,
                _lastTurn?.TurnState ?? Answered);

        private void Activity(Entry entry, int number)
        {
            _lastActivityLine = number;
            _lastActivity = IsoTime.ParseIso(entry.Timestamp) ?? _lastActivity;
            if (entry.Type == "user" && entry.TypedPrompt is { } prompt)
            {
                _promptCount++;
                _firstPrompt = NonEmpty(_firstPrompt) ?? prompt;
            }

            if (entry.Type is "user" or "assistant" && !entry.Meta)
            {
                _lastTurn = entry;
            }
        }
    }
}
