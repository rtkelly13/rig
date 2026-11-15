using System.Globalization;
using System.Text.Json;

namespace Rig.Core.Estate;

/// <summary>The decisions feed as the web console and the ops tools show it.</summary>
/// <param name="Decisions">Each thing waiting on a person; empty when nothing does.</param>
/// <param name="Written">When the feed was last written, ISO 8601 UTC; empty when there is no feed.</param>
/// <param name="Note">Why the list is empty when it is not because nothing needs deciding; empty otherwise.</param>
public sealed record DecisionFeedView(IReadOnlyList<Decision> Decisions, string Written, string Note);

/// <summary>
/// Reads the feed <c>rig estate decisions --write</c> leaves. A missing file is normal before the job's first run,
/// so it is an empty list with a note, not an error.
/// </summary>
public static class DecisionFeed
{
    /// <summary>The feed at <paramref name="path"/>, or an empty one saying why there is nothing to show.</summary>
    public static DecisionFeedView Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DecisionFeedView([], string.Empty, $"Cannot read the decisions feed {path}: {e.Message}");
        }

        if (!File.Exists(path))
        {
            return new DecisionFeedView([], string.Empty, $"No decisions feed yet: the estate-decisions job has not written {path}.");
        }

        var stamp = new DateTimeOffset(written, TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        if (Parse(path) is not { } decisions)
        {
            return new DecisionFeedView([], stamp, $"The decisions feed {path} is not in the expected shape.");
        }

        return decisions.Count == 0
            ? new DecisionFeedView([], stamp, "Nothing needs deciding.")
            : new DecisionFeedView(decisions, stamp, string.Empty);
    }

    private static List<Decision>? Parse(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var decisions = new List<Decision>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || Text(item, "kind") is not { } kind
                    || Text(item, "repo") is not { } repo
                    || Text(item, "path") is not { } where
                    || Text(item, "reason") is not { } reason
                    || !item.TryGetProperty("size_bytes", out var size)
                    || size.ValueKind != JsonValueKind.Number
                    || !size.TryGetInt64(out var bytes)
                    || Text(item, "suggested_action") is not { } action
                    || Text(item, "command") is not { } command)
                {
                    return null;
                }

                decisions.Add(new Decision(kind, repo, where, reason, bytes, action, command));
            }

            return decisions;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
