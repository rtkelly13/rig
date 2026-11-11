using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Rig.Core.Orca;

namespace Rig.Core.Tests.Orca;

/// <summary>
/// Orca's runtime on the wire, held in memory: projects, groups, folder workspaces and worktrees, changed by the calls
/// that change them in Orca. Keepalives and another caller's answer come first, as Orca interleaves them.
/// </summary>
public sealed class FakeOrca : IOrcaExchange
{
    /// <summary>The token every request must carry.</summary>
    public const string Token = "secret-token";

    /// <summary>A day in milliseconds.</summary>
    public const double DayMs = 86_400_000.0;

    /// <summary>The "now" worktree activity is measured from.</summary>
    public const double NowMs = 100 * DayMs;

    /// <summary>The transport every request must go to.</summary>
    public static readonly OrcaTransport Transport = new(OrcaTransport.Unix, "/tmp/orca.sock");

    /// <summary>Where this Orca listens; <see cref="Transport"/> unless several fakes run side by side.</summary>
    public OrcaTransport Listening { get; init; } = Transport;

    /// <summary>The <c>orca-runtime.json</c> Orca writes at start-up, naming <see cref="Listening"/> and the token.</summary>
    public string Metadata => new JsonObject
    {
        ["authToken"] = Token,
        ["transports"] = new JsonArray(new JsonObject { ["kind"] = Listening.Kind, ["endpoint"] = Listening.Endpoint }),
    }.ToJsonString();

    /// <summary><c>repo.list</c>.</summary>
    public List<JsonObject> Repos { get; } = [];

    /// <summary><c>projectGroup.list</c>.</summary>
    public List<JsonObject> Groups { get; } = [];

    /// <summary><c>folderWorkspace.list</c>.</summary>
    public List<JsonObject> FolderWorkspaces { get; } = [];

    /// <summary><c>worktree.list</c>.</summary>
    public List<JsonObject> Worktrees { get; } = [];

    /// <summary><c>worktree.ps</c>.</summary>
    public List<JsonObject> Activity { get; } = [];

    /// <summary>Every call, in order, with its parameters.</summary>
    public List<(string Method, JsonObject? Params)> Calls { get; } = [];

    /// <summary>The worktree ids <c>worktree.rm</c> was asked to remove.</summary>
    public List<string> Removed { get; } = [];

    /// <summary>Worktree ids whose removal Orca accepts but does not finish: it answers <c>removed: false</c> and keeps listing them.</summary>
    public HashSet<string> Stuck { get; } = [];

    /// <summary>A client for this fake.</summary>
    public OrcaRuntime Runtime() => new(Listening, Token, this);

    /// <summary>A project as <c>repo.list</c> reports it.</summary>
    public static JsonObject Project(string id, string path, string displayName, string? groupId = null, string? owner = null) => new()
    {
        ["id"] = id,
        ["path"] = path,
        ["displayName"] = displayName,
        ["projectGroupId"] = groupId,
        ["gitRemoteIdentity"] = owner is null ? null : new JsonObject { ["canonicalKey"] = $"github.com/{owner}/{displayName}" },
        ["badgeColor"] = "#737373",
    };

    /// <summary>A group as <c>projectGroup.list</c> reports it.</summary>
    public static JsonObject Group(string id, string name, string? parentId = null, string? parentPath = null)
    {
        var group = new JsonObject { ["id"] = id, ["name"] = name, ["parentGroupId"] = parentId };
        if (parentPath is not null)
        {
            group["parentPath"] = parentPath;
        }

        return group;
    }

    /// <summary>Lists a linked worktree of project <c>r1</c> at <paramref name="path"/>; returns its id.</summary>
    public string AddWorktree(string path, string branch, double idleDays = 10)
    {
        var posix = path.Replace('\\', '/');
        var id = $"r1::{posix}";
        Worktrees.Add(new JsonObject
        {
            ["id"] = id,
            ["repoId"] = "r1",
            ["path"] = posix,
            ["branch"] = branch.Length > 0 ? $"refs/heads/{branch}" : string.Empty,
            ["isMainWorktree"] = false,
            ["lastActivityAt"] = NowMs - (idleDays * DayMs),
        });
        return id;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ExchangeAsync(OrcaTransport transport, string request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        Assert.Equal(Listening, transport);
        var frame = JsonNode.Parse(request)!.AsObject();
        Assert.Equal(Token, (string?)frame["authToken"]);
        var method = (string)frame["method"]!;
        var parameters = frame["params"] as JsonObject;
        Calls.Add((method, parameters?.DeepClone().AsObject()));
        var result = Answer(method, parameters ?? []);
        yield return """{"_keepalive": true}""";
        yield return """{"id": "someone-else", "ok": true, "result": {}}""";
        yield return new JsonObject { ["id"] = frame["id"]!.DeepClone(), ["ok"] = true, ["result"] = result }.ToJsonString();
    }

    private JsonObject Answer(string method, JsonObject parameters)
    {
        switch (method)
        {
            case "repo.list":
                return new() { ["repos"] = Array(Repos) };
            case "projectGroup.list":
                return new() { ["groups"] = Array(Groups) };
            case "folderWorkspace.list":
                return new() { ["folderWorkspaces"] = Array(FolderWorkspaces) };
            case "worktree.list":
                return new() { ["worktrees"] = Array(Worktrees) };
            case "worktree.ps":
                return new() { ["worktrees"] = Array(Activity), ["truncated"] = false };
            case "worktree.rm":
            {
                var id = Selector(parameters, "worktree");
                Removed.Add(id);
                var done = !Stuck.Contains(id);
                if (done)
                {
                    _ = Worktrees.RemoveAll(w => (string?)w["id"] == id);
                }

                return new() { ["removed"] = done };
            }

            case "repo.add":
            {
                var path = (string)parameters["path"]!;
                var name = Path.GetFileName(path.TrimEnd('/', '\\'));
                var repo = Project($"r-{name}", path, name, owner: "rtkelly-labs");
                Repos.Add(repo);
                return new() { ["repo"] = repo.DeepClone() };
            }

            case "projectGroup.create":
            {
                var group = Group($"g-{(string)parameters["name"]!}", (string)parameters["name"]!, (string?)parameters["parentGroupId"]);
                Groups.Add(group);
                return new() { ["group"] = group.DeepClone() };
            }

            case "repo.rm":
            {
                var id = Selector(parameters, "repo");
                _ = Repos.RemoveAll(r => (string?)r["id"] == id);
                return new() { ["removed"] = true };
            }

            case "projectGroup.moveProject":
            {
                var id = Selector(parameters, "repo");
                var moved = Repos.Single(r => (string?)r["id"] == id);
                moved["projectGroupId"] = (string?)parameters["groupId"];
                return new() { ["repo"] = moved.DeepClone() };
            }

            case "projectGroup.delete":
                return new() { ["deleted"] = DeleteGroup((string)parameters["groupId"]!) };
            default:
                throw new InvalidOperationException($"FakeOrca does not answer {method}");
        }
    }

    // As Orca does: the group and its subgroups go, and their projects move to the top level.
    private bool DeleteGroup(string groupId)
    {
        var doomed = new HashSet<string>(StringComparer.Ordinal) { groupId };
        while (Groups.Where(g => (string?)g["parentGroupId"] is { } parent && doomed.Contains(parent)).Select(g => (string)g["id"]!).Where(id => !doomed.Contains(id)).ToList() is { Count: > 0 } grown)
        {
            doomed.UnionWith(grown);
        }

        var before = Groups.Count;
        _ = Groups.RemoveAll(g => doomed.Contains((string)g["id"]!));
        foreach (var repo in Repos.Where(r => (string?)r["projectGroupId"] is { } group && doomed.Contains(group)))
        {
            repo["projectGroupId"] = null;
        }

        return Groups.Count < before;
    }

    private static string Selector(JsonObject parameters, string key)
    {
        var value = (string)parameters[key]!;
        return value.StartsWith("id:", StringComparison.Ordinal) ? value[3..] : value;
    }

    private static JsonArray Array(IEnumerable<JsonObject> items) => [.. items.Select(item => item.DeepClone())];
}
