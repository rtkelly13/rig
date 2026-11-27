using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Text;

namespace Rig.Core.ReviewQueue;

/// <summary>Strict, paginated triage observations through the existing authenticated GitHub API port.</summary>
/// <param name="github">The one GitHub transport, with its timeout, encoding and tracing.</param>
public sealed class QueueGitHub(IGitHubApi github) : IQueueSource
{
    /// <inheritdoc/>
    public IReadOnlyList<string> Repositories(string registry)
    {
        var parsed = JsonSerializer.Deserialize<Registry>(QueueProjection.Read(registry), QueueJson.Options);
        var repos = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var candidate in parsed.Projects)
        {
            var project = candidate ?? throw new JsonException("Null managed project");
            if (!project.Enabled || project.Archived)
            {
                continue;
            }

            var slug = RepoSlug.FromRemote(project.Repo) ?? RepoSlug.FromPath(project.Repo)
                ?? throw new FormatException($"Not a GitHub repository: {project.Repo}");
            repos.Add(PrRef.Parse($"{slug.FullName}#1").Slug);
        }

        return repos.Count > 0 ? [.. repos] : throw new FormatException("No enabled managed repositories");
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QueueItem>> FetchAsync(string repo, string kind, CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        var reference = PrRef.Parse($"{repo}#1");
        var connectionName = kind == "pr" ? "pullRequests" : "issues";
        var query = "query($owner:String!,$name:String!,$after:String) { repository(owner:$owner,name:$name) { "
            + $"items:{connectionName}(first:25,after:$after,states:OPEN) {{ nodes {{ {QueueSnapshot.Fields(kind, detailed: false)} }} "
            + "pageInfo { hasNextPage endCursor } } } }";
        var items = new List<QueueItem>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        using var deadline = Deadline(cancellationToken);
        while (true)
        {
            var request = ApiRequest.GraphQL(query, Variables(reference, after));
            var repository = await RepositoryAsync(request, repo, deadline.Token).ConfigureAwait(false);
            var connection = JsonShape.Object(repository["items"]);
            if (!connection.ContainsKey("nodes"))
            {
                throw new FormatException("Missing GitHub item list");
            }

            foreach (var node in JsonShape.ItemsWithNulls(connection, "nodes"))
            {
                items.Add(QueueSnapshot.Item(node ?? throw new FormatException("Null GitHub item"), reference.Slug, kind));
            }

            var page = JsonShape.Object(connection["pageInfo"]);
            if (!page.ContainsKey("hasNextPage"))
            {
                throw new FormatException("Missing GitHub pagination state");
            }

            if (!JsonShape.Bool(page, "hasNextPage"))
            {
                return items;
            }

            after = JsonShape.OptionalString(page, "endCursor");
            if (string.IsNullOrEmpty(after) || !cursors.Add(after))
            {
                throw new FormatException($"Incomplete GitHub pagination for {repo}");
            }
        }
    }

    /// <inheritdoc/>
    public async Task<QueueItem> ObserveAsync(QueueItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateKind(item.Kind);
        var reference = PrRef.Parse(item.Reference);
        var field = item.Kind == "pr" ? "pullRequest" : "issue";
        var query = "query($owner:String!,$name:String!,$number:Int!) { repository(owner:$owner,name:$name) { "
            + $"item:{field}(number:$number) {{ {QueueSnapshot.Fields(item.Kind, detailed: true)} }} }} }}";
        var variables = Variables(reference);
        variables["number"] = reference.Number;
        var repository = await RepositoryAsync(ApiRequest.GraphQL(query, variables), item.Reference, cancellationToken).ConfigureAwait(false);
        return QueueSnapshot.Item(JsonShape.Object(repository["item"]), item.Repo, item.Kind) with { InScope = item.InScope };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QueueActivity>> ActivityAsync(QueueItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateKind(item.Kind);
        var reference = PrRef.Parse(item.Reference);
        var number = reference.Number.ToString(CultureInfo.InvariantCulture);
        List<string> routes = [$"issues/{number}/comments"];
        if (item.Kind == "pr")
        {
            routes.Add($"pulls/{number}/reviews");
            routes.Add($"pulls/{number}/comments");
        }

        var result = new List<QueueActivity>();
        foreach (var route in routes)
        {
            using var deadline = Deadline(cancellationToken);
            for (var page = 1; ; page++)
            {
                var response = await CallAsync(ApiRequest.Get(string.Create(CultureInfo.InvariantCulture,
                    $"repos/{reference.Slug}/{route}?per_page=100&page={page}")), deadline.Token).ConfigureAwait(false);
                var rows = JsonNode.Parse(response) as JsonArray ?? throw new FormatException("Expected GitHub discussion list");
                foreach (var node in rows)
                {
                    var discussion = JsonShape.Object(node);
                    if (discussion["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var id))
                    {
                        throw new FormatException("Missing or invalid GitHub discussion id");
                    }
                    if (!discussion.ContainsKey("user"))
                    {
                        throw new FormatException("Missing GitHub discussion user");
                    }

                    var author = JsonShape.OptionalObject(discussion, "user");
                    var body = JsonShape.OptionalString(discussion, "body") ?? "";
                    var at = JsonShape.String(discussion, "updated_at");
                    if (at.Length == 0)
                    {
                        at = JsonShape.String(discussion, "submitted_at");
                    }

                    if (at.Length == 0)
                    {
                        continue;
                    }

                    var state = discussion.ContainsKey("state") ? JsonShape.String(discussion, "state") : "COMMENTED";
                    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
                    var key = string.Create(CultureInfo.InvariantCulture, $"{reference.Slug}/{route}/{id}/{at}/{state}/{hash}");
                    var login = author is null ? "deleted-user" : author.ContainsKey("login") ? JsonShape.String(author, "login")
                        : throw new FormatException("Missing GitHub discussion login");
                    result.Add(new QueueActivity(key, at, login, $"github:{state.ToLowerInvariant()}", JsonShape.String(discussion, "commit_id"), body));
                }

                if (rows.Count < 100)
                {
                    break;
                }
            }
        }

        return [.. result.OrderBy(e => e.At, StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal)];
    }

    private static Dictionary<string, object?> Variables(PrRef reference, string? after = null) =>
        new(StringComparer.Ordinal) { ["owner"] = reference.Owner, ["name"] = reference.Repo, ["after"] = after };

    private static void ValidateKind(string kind)
    {
        if (kind is not ("pr" or "issue"))
        {
            throw new ArgumentException("Invalid triage item kind", nameof(kind));
        }
    }

    private static CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(GhApi.DefaultTimeout);
        return deadline;
    }

    private async Task<JsonObject> RepositoryAsync(ApiRequest request, string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ran = await github.CallAsync(request, cancellationToken).ConfigureAwait(false);
        if (RepositoryNotFound(ran.Out))
        {
            throw new MissingRepositoryException($"{reference} does not resolve on GitHub: deleted, renamed or not visible to this login");
        }

        var root = JsonShape.Object(JsonNode.Parse(ran.Code == 0 ? ran.Out : throw Failed(ran)));
        if (root.ContainsKey("errors") && JsonShape.Field(root, "errors") is not JsonArray { Count: 0 })
        {
            throw new GhException($"Incomplete GitHub response for {reference}");
        }

        return JsonShape.Object(JsonShape.Object(root["data"])["repository"]);
    }

    private async Task<string> CallAsync(ApiRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ran = await github.CallAsync(request, cancellationToken).ConfigureAwait(false);
        return ran.Code == 0 ? ran.Out : throw Failed(ran);
    }

    private static GhException Failed(Ran ran) =>
        new(ran.Err.Trim().Length > 0 ? ran.Err.Trim() : $"gh exited {ran.Code}");

    // Only GitHub's own answer counts: a null repository and every error a NOT_FOUND on exactly that path. Network,
    // auth and rate-limit failures carry no such body, and a mixed answer (NOT_FOUND beside FORBIDDEN) may be a
    // permission problem, so both still fail the whole refresh.
    private static bool RepositoryNotFound(string output)
    {
        JsonObject? root;
        try
        {
            root = JsonShape.AsObject(JsonNode.Parse(output));
        }
        catch (JsonException)
        {
            return false;
        }

        var errors = JsonShape.AsArray(JsonShape.Field(root, "errors")) ?? [];
        return JsonShape.FieldObject(root, "data") is { } data && JsonShape.Field(data, "repository") is null
            && errors.Count > 0 && errors.Select(JsonShape.AsObject).All(error => JsonShape.FieldText(error, "type") == "NOT_FOUND"
                && JsonShape.AsArray(JsonShape.Field(error, "path")) is [var step] && JsonShape.AsText(step) == "repository");
    }

    [method: JsonConstructor]
    private readonly record struct Registry(IReadOnlyList<Project?> Projects);
    [method: JsonConstructor]
    private readonly record struct Project(string Repo, bool Enabled = true, bool Archived = false);
}
