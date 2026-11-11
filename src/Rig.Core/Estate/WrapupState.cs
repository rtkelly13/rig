using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>One prompt rig delivered to one agent pane.</summary>
/// <param name="At">When the send was accepted, UTC ISO 8601.</param>
/// <param name="Pane">Orca's <c>tabId:leafId</c> for the pane.</param>
/// <param name="Session">The session's name.</param>
/// <param name="Head">HEAD when asked, so a reader can tell whether the worktree moved on since.</param>
/// <param name="Facts">The facts line rig sent with the prompt.</param>
/// <param name="TemplateSha">First 12 hex digits of the template's SHA-256, so a wording change is visible.</param>
/// <param name="Id">Short token sent in the prompt; <c>estate wrapup report --ask ID</c> names the ask it answers.</param>
public sealed record AskRecord(string At, string Pane, string Session, string Head, string Facts, string TemplateSha, string Id = "");

/// <summary>One verdict: reported by the agent with <c>estate wrapup report</c>, or read back from its <c>WRAPUP:</c> line.</summary>
/// <param name="At">When it was recorded, UTC ISO 8601.</param>
/// <param name="Pane">The pane that answered.</param>
/// <param name="Verdict"><c>done</c> or <c>pending</c>.</param>
/// <param name="Detail">What is left, for a pending verdict.</param>
/// <param name="Head">The commit the verdict speaks for.</param>
/// <param name="AskedAt">The <c>at</c> of the ask it answers; null when rig has no record of asking.</param>
/// <param name="Todo">What the agent says is left for its owner, one item each; only <c>report</c> fills it.</param>
/// <param name="Via"><c>report</c> when the agent ran <c>estate wrapup report</c>, <c>reply</c> when rig parsed its last message.</param>
public sealed record AnswerRecord(
    string At, string Pane, string Verdict, string Detail, string Head, string? AskedAt = null, IReadOnlyList<string>? Todo = null, string Via = "reply")
{
    /// <summary>What the agent says is left for its owner.</summary>
    public IReadOnlyList<string> Todo { get; init; } = Todo ?? [];
}

/// <summary>
/// What <c>estate wrapup</c> remembers per worktree: <c>temp/.rig/wrapup.json</c>, schema <c>rig.wrapup/1</c>. Ported from
/// <c>src/rig/estate/wrapup_state.py</c>, which the triage inbox still reads, so the file stays byte-compatible.
/// </summary>
/// <param name="Schema">The schema the file carries.</param>
/// <param name="WorktreeId">Orca's id for the worktree.</param>
/// <param name="Branch">The worktree's branch when the file was started.</param>
/// <param name="Asks">Every ask, oldest first.</param>
/// <param name="Answers">Every answer, oldest first.</param>
public sealed record WrapupState(string Schema, string WorktreeId, string Branch, IReadOnlyList<AskRecord> Asks, IReadOnlyList<AnswerRecord> Answers)
{
    /// <summary>The feature name: the file is <c>temp/.rig/wrapup.json</c>.</summary>
    public const string Feature = "wrapup";

    /// <summary>The schema this reads and writes.</summary>
    public const string CurrentSchema = "rig.wrapup/1";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A state with nothing recorded.</summary>
    public static WrapupState Empty(string worktreeId = "", string branch = "") => new(CurrentSchema, worktreeId, branch, [], []);

    /// <summary>The last ask sent to <paramref name="pane"/>.</summary>
    public AskRecord? LastAsk(string pane) => Asks.LastOrDefault(a => a.Pane == pane);

    /// <summary>The last answer from <paramref name="pane"/>.</summary>
    public AnswerRecord? LastAnswer(string pane) => Answers.LastOrDefault(a => a.Pane == pane);

    /// <summary>The newest ask carrying <paramref name="askId"/>.</summary>
    public AskRecord? AskById(string askId) => string.IsNullOrEmpty(askId) ? null : Asks.LastOrDefault(a => a.Id == askId);

    /// <summary>The newest answer recorded for <paramref name="ask"/>, from either source.</summary>
    public AnswerRecord? AnswerTo(AskRecord ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        return Answers.LastOrDefault(a => a.AskedAt == ask.At && a.Pane == ask.Pane);
    }

    /// <summary>This state with <paramref name="ask"/> appended.</summary>
    public WrapupState WithAsk(AskRecord ask) => this with { Asks = [.. Asks, ask] };

    /// <summary>This state with <paramref name="answer"/> appended.</summary>
    public WrapupState WithAnswer(AnswerRecord answer) => this with { Answers = [.. Answers, answer] };

    /// <summary>The file's text as pydantic wrote it: compact JSON, every field present, without the trailing newline.</summary>
    public string Encode() => new JsonObject
    {
        ["schema"] = Schema,
        ["worktree_id"] = WorktreeId,
        ["branch"] = Branch,
        ["asks"] = new JsonArray([.. Asks.Select(a => (JsonNode)new JsonObject
        {
            ["at"] = a.At,
            ["pane"] = a.Pane,
            ["session"] = a.Session,
            ["head"] = a.Head,
            ["facts"] = a.Facts,
            ["template_sha"] = a.TemplateSha,
            ["id"] = a.Id,
        })]),
        ["answers"] = new JsonArray([.. Answers.Select(a => (JsonNode)new JsonObject
        {
            ["at"] = a.At,
            ["pane"] = a.Pane,
            ["verdict"] = a.Verdict,
            ["detail"] = a.Detail,
            ["head"] = a.Head,
            ["asked_at"] = a.AskedAt,
            ["todo"] = new JsonArray([.. a.Todo.Select(t => (JsonNode)t)]),
            ["via"] = a.Via,
        })]),
    }.ToJsonString(Compact);

    /// <summary>The state in <paramref name="text"/>, or null when it is not JSON or does not fit the shape.</summary>
    public static WrapupState? Decode(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root)
            {
                return null;
            }

            return new WrapupState(
                Text(root, "schema", CurrentSchema),
                Text(root, "worktree_id", ""),
                Text(root, "branch", ""),
                [.. List(root, "asks").Select(node => node is JsonObject a
                    ? new AskRecord(Text(a, "at"), Text(a, "pane"), Text(a, "session"), Text(a, "head"), Text(a, "facts"), Text(a, "template_sha"), Text(a, "id", ""))
                    : throw new InvalidDataException("ask"))],
                [.. List(root, "answers").Select(node => node is JsonObject a
                    ? new AnswerRecord(
                        Text(a, "at"),
                        Text(a, "pane"),
                        Text(a, "verdict"),
                        Text(a, "detail"),
                        Text(a, "head"),
                        a.TryGetPropertyValue("asked_at", out var asked) && asked is not null ? Text(a, "asked_at") : null,
                        [.. List(a, "todo").Select(t => t is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : throw new InvalidDataException("todo"))],
                        Text(a, "via", "reply"))
                    : throw new InvalidDataException("answer"))]);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Text(JsonObject parent, string key, string? fallback = null)
    {
        if (!parent.TryGetPropertyValue(key, out var node))
        {
            return fallback ?? throw new InvalidDataException(key);
        }

        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : throw new InvalidDataException(key);
    }

    private static JsonArray List(JsonObject parent, string key) =>
        !parent.TryGetPropertyValue(key, out var node) ? [] : node as JsonArray ?? throw new InvalidDataException(key);
}

/// <summary>Where <c>estate wrapup</c> keeps its state: one file per worktree.</summary>
public interface IWrapupStore
{
    /// <summary>The worktree's state, or null when there is none or it cannot be read.</summary>
    WrapupState? Read(string worktree);

    /// <summary>Replaces the worktree's state; null when written, else why not.</summary>
    Task<string?> WriteAsync(string worktree, WrapupState state, CancellationToken cancellationToken = default);
}

/// <summary><c>&lt;worktree&gt;/temp/.rig/wrapup.json</c>. Unreadable files and unknown schemas read as absent.</summary>
/// <param name="git">Asks git whether the state folder is ignored.</param>
public sealed class FileWrapupStore(Git git) : IWrapupStore
{
    /// <inheritdoc/>
    public WrapupState? Read(string worktree)
    {
        var text = WorktreeState.ReadText(worktree, WrapupState.Feature);
        var state = string.IsNullOrEmpty(text) ? null : WrapupState.Decode(text);
        return state is not null && state.Schema == WrapupState.CurrentSchema ? state : null;
    }

    /// <inheritdoc/>
    public Task<string?> WriteAsync(string worktree, WrapupState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return WorktreeState.WriteTextAsync(git, worktree, WrapupState.Feature, state.Encode() + "\n", cancellationToken);
    }
}

/// <summary>
/// Where rig keeps state about one worktree: <c>&lt;worktree&gt;/temp/.rig/</c>. Ported from <c>src/rig/worktree_state.py</c>
/// (docs/repo-governance/worktree-state.md): one file per feature, written only where git ignores it, replaced atomically.
/// </summary>
public static class WorktreeState
{
    /// <summary>The folder, relative to a worktree's root.</summary>
    public const string StateDir = "temp/.rig";

    // git check-ignore exits 1 for "not ignored" and 128 when the folder is not a git worktree.
    private const int NotIgnored = 1;

    /// <summary><c>&lt;worktree&gt;/temp/.rig/&lt;feature&gt;.json</c>.</summary>
    public static string StateFile(string worktree, string feature) => Path.Join(worktree, "temp", ".rig", $"{feature}.json");

    /// <summary>The feature's file, or null when it is missing or unreadable.</summary>
    public static string? ReadText(string worktree, string feature)
    {
        try
        {
            return File.ReadAllText(StateFile(worktree, feature), Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether git ignores each of <paramref name="names"/> in the state folder: <c>yes</c>, <c>no</c> or <c>not-a-repo</c>.</summary>
    public static async Task<string> IgnoredAsync(Git git, string worktree, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(names);
        if (!Directory.Exists(worktree))
        {
            return "not-a-repo";
        }

        foreach (var name in names.Count > 0 ? names : ["probe.json"])
        {
            var ran = await git.RunAsync(worktree, ["check-ignore", "--quiet", "--no-index", $"{StateDir}/{name}"], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!ran.Ok)
            {
                return ran.Code == NotIgnored ? "no" : "not-a-repo";
            }
        }

        return "yes";
    }

    /// <summary>Replaces the feature's file. Returns null on success, or why nothing was written.</summary>
    public static async Task<string?> WriteTextAsync(Git git, string worktree, string feature, string text, CancellationToken cancellationToken = default)
    {
        var target = StateFile(worktree, feature);
        var name = Path.GetFileName(target);
        var staging = Path.Join(Path.GetDirectoryName(target), $".{name}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp");
        switch (await IgnoredAsync(git, worktree, [name, Path.GetFileName(staging)], cancellationToken).ConfigureAwait(false))
        {
            case "no":
                return $"{StateDir}/{name} is not gitignored here";
            case "not-a-repo":
                return "not a git worktree";
            default:
                break;
        }

        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(staging, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(staging, target, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            File.Delete(staging);
            return $"cannot write {target.Replace('\\', '/')}: {e.Message}";
        }

        return null;
    }
}

/// <summary>Timestamps and ids as <c>wrapup_state.py</c> wrote them.</summary>
public static class WrapupStamps
{
    /// <summary>Eight hex digits: enough to tell one worktree's asks apart, short enough to type.</summary>
    public static string NewAskId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>Python's <c>isoformat(timespec="milliseconds")</c> in UTC: <c>2026-01-02T03:04:05.678+00:00</c>.</summary>
    public static string Timestamp(DateTimeOffset when) => when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+00:00'", CultureInfo.InvariantCulture);

    /// <summary>The instant <paramref name="text"/> names, or null when it is not an ISO 8601 timestamp.</summary>
    public static DateTimeOffset? Parse(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
