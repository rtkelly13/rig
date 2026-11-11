using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// Holds <see cref="VaultGraph"/> to <c>rig.obsidian.link_manager</c> and <c>orphan_manager</c>. <c>graph-oracle.json</c>
/// records, for each synthetic vault, every graph command run on a fresh copy (the writers twice on the same copy): the
/// lines handed to the log, the result, and every file afterwards. The Python's desktop-app orphan list was stubbed empty;
/// here the app's settings are missing from an empty home, so the same scan runs. The log is compared as a multiset, since
/// the Python listed unlinked mentions in directory order, and without its Rich markup, which C# does not print.
/// The <c>strict</c> vault, where the Python crashed, and line endings in <c>links fix</c> are deliberate differences;
/// <see cref="VaultGraphTests"/> holds them.
/// </summary>
public sealed class VaultGraphOracleTests
{
    [Fact]
    public async Task Runs_every_recorded_graph_command_as_python_did()
    {
        var mismatches = new List<string>();
        foreach (var node in JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Vault", "graph-oracle.json"), TestContext.Current.CancellationToken))!.AsArray())
        {
            var oracle = node!.AsObject();
            if ((oracle["posix_only"]!.GetValue<bool>() && OperatingSystem.IsWindows()) || oracle["name"]!.GetValue<string>() == "strict")
            {
                continue;
            }

            var files = oracle["files"]!.AsArray();
            var originals = files
                .Select(f => f!.AsObject())
                .Where(f => f["b64"] is not null)
                .ToDictionary(f => f["path"]!.GetValue<string>(), f => Convert.FromBase64String(f["b64"]!.GetValue<string>()), StringComparer.Ordinal);
            foreach (var run in oracle["runs"]!.AsArray().Select(r => r!.AsObject()))
            {
                mismatches.AddRange(await Run($"{oracle["name"]} {run["op"]}", files, originals, run));
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} difference(s) from the Python:\n{string.Join('\n', mismatches.Take(60))}");
    }

    private static async Task<List<string>> Run(string label, JsonArray files, Dictionary<string, byte[]> originals, JsonObject run)
    {
        var parent = RepoTemp.NewVaultParent("rig-graph");
        var root = Path.Combine(parent, "Vault");
        var op = run["op"]!.GetValue<string>();
        try
        {
            VaultLintOracleTests.Materialize(root, files);
            var differences = new List<string>();
            var pass = 0;
            foreach (var expected in run["passes"]!.AsArray().Select(p => p!.AsObject()))
            {
                var at = $"{label} pass {++pass}";
                var said = new List<string>();
                var graph = new VaultGraph(root, said.Add, new ObsidianOrphans(new FakeProc(), Path.Combine(parent, "home")));
                JsonNode? result = op switch
                {
                    "links-list" => Report(graph.ListLinks()),
                    "links-fix" => graph.FixLinks() is var f ? new JsonObject { ["broken_found"] = f.BrokenFound, ["fixed"] = f.Fixed } : null,
                    "orphans-list" => new JsonArray([.. (await graph.ListOrphansAsync()).Select(o => JsonValue.Create(o))]),
                    "orphans-link" => await graph.LinkOrphansAsync() is var l ? new JsonObject { ["orphans_found"] = l.OrphansFound, ["linked"] = l.Linked } : null,
                    _ => await graph.RootHubsAsync() is var h ? new JsonObject { ["hubs_created"] = h.HubsCreated, ["links_added"] = h.LinksAdded } : null,
                };

                differences.AddRange(Result(at, result, expected["result"]));
                differences.AddRange(Multiset($"{at} log", said, [.. expected["log"]!.AsArray().Select(l => Unmarked(l!.GetValue<string>()))]));
                differences.AddRange(After(at, root, originals, expected["after"]!.AsObject(), lineEndingsKept: op == "links-fix"));
            }

            return differences;
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static JsonObject Report(LinkReport report) => new()
    {
        ["broken"] = new JsonArray([.. report.Broken.Select(b => new JsonObject { ["source"] = b.Source, ["target"] = b.Target, ["line"] = b.Line, ["context"] = b.Context })]),
        ["unlinked"] = new JsonArray([.. report.Unlinked.Select(u => new JsonObject { ["source"] = u.Source, ["mention"] = u.Mention, ["target"] = u.Target })]),
    };

    // Unlinked mentions came in directory order, so they are compared as a multiset; everything else exactly.
    private static IEnumerable<string> Result(string label, JsonNode? actual, JsonNode? expected)
    {
        if (actual is JsonObject { } report && report["unlinked"] is JsonArray got && expected?["unlinked"] is JsonArray want)
        {
            foreach (var difference in Multiset($"{label} unlinked", [.. got.Select(n => n!.ToJsonString())], [.. want.Select(n => n!.ToJsonString())]))
            {
                yield return difference;
            }

            actual = new JsonObject { ["broken"] = report["broken"]!.DeepClone() };
            expected = new JsonObject { ["broken"] = expected["broken"]!.DeepClone() };
        }

        if (!JsonNode.DeepEquals(actual, expected))
        {
            yield return $"{label}: result {actual?.ToJsonString()}, Python {expected?.ToJsonString()}";
        }
    }

    private static string Unmarked(string line)
    {
        foreach (var tag in new[] { "[bold red]", "[/bold red]", "[bold yellow]", "[/bold yellow]" })
        {
            line = line.Replace(tag, string.Empty, StringComparison.Ordinal);
        }

        return line;
    }

    private static IEnumerable<string> After(string label, string root, Dictionary<string, byte[]> originals, JsonObject expected, bool lineEndingsKept)
    {
        var actual = Snapshot(root);
        foreach (var (path, value) in expected)
        {
            if (!actual.Remove(path, out var got))
            {
                yield return $"{label}: missing {path}";
                continue;
            }

            if (value!["link"] is { } link)
            {
                if (got.Link != link.GetValue<string>())
                {
                    yield return $"{label}: {path} links to {got.Link}, Python left a link to {link}";
                }

                continue;
            }

            var want = value["same"] is not null ? originals[path] : Convert.FromBase64String(value["b64"]!.GetValue<string>());
            var bytes = got.Bytes ?? [];
            if (lineEndingsKept && originals.TryGetValue(path, out var original) && original.AsSpan().IndexOf("\r\n"u8) >= 0)
            {
                // The Python rewrote a fixed note with \n endings; the C# keeps the note's own.
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal));
            }

            if (!bytes.AsSpan().SequenceEqual(want))
            {
                yield return $"{label}: {path} is {Encoding.UTF8.GetString(bytes).ReplaceLineEndings("⏎")}, Python left {Encoding.UTF8.GetString(want).ReplaceLineEndings("⏎")}";
            }
        }

        foreach (var path in actual.Keys)
        {
            yield return $"{label}: unexpected {path}";
        }
    }

    private static Dictionary<string, (string? Link, byte[]? Bytes)> Snapshot(string root)
    {
        var files = new Dictionary<string, (string? Link, byte[]? Bytes)>(StringComparer.Ordinal);
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var folder))
        {
            foreach (var full in Directory.EnumerateFileSystemEntries(folder))
            {
                var rel = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
                if (new FileInfo(full).LinkTarget is { } target)
                {
                    files[rel] = (target, null);
                }
                else if (Directory.Exists(full))
                {
                    pending.Push(full);
                }
                else
                {
                    files[rel] = (null, File.ReadAllBytes(full));
                }
            }
        }

        return files;
    }

    private static IEnumerable<string> Multiset(string label, List<string> actual, List<string> expected)
    {
        var left = actual.CountBy(s => s).ToDictionary(StringComparer.Ordinal);
        var right = expected.CountBy(s => s).ToDictionary(StringComparer.Ordinal);
        foreach (var key in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
        {
            var (got, want) = (left.GetValueOrDefault(key), right.GetValueOrDefault(key));
            if (got != want)
            {
                yield return $"{label}: {key.ReplaceLineEndings("⏎")} x{got}, Python x{want}";
            }
        }
    }
}
