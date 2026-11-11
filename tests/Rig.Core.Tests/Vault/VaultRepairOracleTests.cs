using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// Holds <see cref="VaultRepair"/> to <c>rig.obsidian.lint_manager.VaultLinter</c>'s repairs. <c>writers-oracle.json</c>
/// records, for lint's synthetic vaults and one built for the writers, every repair run dry and applied on a fresh copy:
/// the lines printed, the errors logged (cut at the first <c>": "</c>) and every file afterwards. The Python visited notes in
/// directory order, so lines and logs are compared as multisets. A file is compared by what python-frontmatter reads
/// from it, its keys' kinds and its stripped body, since the Python re-dumped the frontmatter the C# edits in place.
/// </summary>
public sealed partial class VaultRepairOracleTests
{
    private const long Stamp = 1700000000;
    private const string FixedCreated = "2023-11-14 22:13";

    [Fact]
    public void Repairs_every_recorded_vault_as_python_did()
    {
        var mismatches = new List<string>();
        foreach (var node in JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vault", "writers-oracle.json")))!.AsArray())
        {
            var oracle = node!.AsObject();
            if (oracle["posix_only"]!.GetValue<bool>() && OperatingSystem.IsWindows())
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
                var label = $"{oracle["name"]} {run["op"]} {(run["apply"]!.GetValue<bool>() ? "apply" : "dry")}";
                mismatches.AddRange(Run(label, files, originals, run));
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} difference(s) from the Python:\n{string.Join('\n', mismatches.Take(60))}");
    }

    private static List<string> Run(string label, JsonArray files, Dictionary<string, byte[]> originals, JsonObject run)
    {
        var parent = RepoTemp.NewVaultParent("rig-repair");
        var root = Path.Combine(parent, "Vault");
        try
        {
            VaultLintOracleTests.Materialize(root, files);
            foreach (var path in originals.Keys)
            {
                File.SetLastWriteTimeUtc(Path.Combine(root, path), DateTimeOffset.FromUnixTimeSeconds(Stamp).UtcDateTime);
            }

            var said = new StringBuilder();
            var logged = new List<string>();
            var repair = new VaultRepair(root, line => said.Append(line).Append('\n'), logged.Add, new FixedClock());
            var dryRun = !run["apply"]!.GetValue<bool>();
            switch (run["op"]!.GetValue<string>())
            {
                case "filenames":
                    repair.CleanupFilenames(dryRun);
                    break;
                case "backfill":
                    repair.BackfillTypes(dryRun);
                    break;
                case "repair":
                    repair.RepairMetadata(dryRun);
                    break;
                case "normalize":
                    repair.BackfillTypes(dryRun);
                    repair.RepairMetadata(dryRun);
                    break;
                default:
                    repair.CleanupSocialDuplicates(dryRun);
                    repair.CleanupResourceDuplicates(dryRun);
                    break;
            }

            var lines = said.ToString().Split('\n').ToList();
            if (lines is [.., ""])
            {
                lines.RemoveAt(lines.Count - 1);
            }

            var logs = logged.Select(l => l.Replace(root, "<vault>", StringComparison.Ordinal).Split(": ", 2)[0].Replace(Path.DirectorySeparatorChar, '/')).ToList();
            var differences = Multiset($"{label} out", lines, Strings(run["out"]!))
                .Concat(Multiset($"{label} log", logs, Strings(run["log"]!)))
                .Concat(After(label, root, originals, run["after"]!.AsObject()))
                .ToList();
            return differences;
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static IEnumerable<string> After(string label, string root, Dictionary<string, byte[]> originals, JsonObject expected)
    {
        var actual = Snapshot(root);
        foreach (var (path, value) in expected)
        {
            if (!actual.Remove(path, out var got))
            {
                yield return $"{label}: missing {path}";
                continue;
            }

            if (value!["same"] is not null)
            {
                if (!File.ReadAllBytes(Path.Join(root, path)).AsSpan().SequenceEqual(originals[path]))
                {
                    yield return $"{label}: {path} changed, Python left it as it was";
                }
            }
            else if (!JsonNode.DeepEquals(got, value))
            {
                yield return $"{label}: {path} is {got?.ToJsonString()}, Python left {value.ToJsonString()}";
            }
        }

        foreach (var path in actual.Keys)
        {
            yield return $"{label}: unexpected {path}";
        }
    }

    private static Dictionary<string, JsonNode> Snapshot(string root)
    {
        var files = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var folder))
        {
            foreach (var full in Directory.EnumerateFileSystemEntries(folder))
            {
                var rel = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
                var info = new FileInfo(full);
                if (info.LinkTarget is { } target)
                {
                    files[rel] = new JsonObject { ["link"] = target };
                }
                else if (Directory.Exists(full))
                {
                    pending.Push(full);
                }
                else
                {
                    files[rel] = Describe(File.ReadAllBytes(full));
                }
            }
        }

        return files;
    }

    // What the oracle records of a file the Python changed: its frontmatter keys' kinds and stripped body, or its bytes.
    private static JsonObject Describe(byte[] data)
    {
        string text;
        try
        {
            text = Frontmatter.Decode(data);
        }
        catch (DecoderFallbackException)
        {
            return new JsonObject { ["b64"] = Convert.ToBase64String(data) };
        }

        var read = Frontmatter.Parse(text);
        if (read.Error is not null)
        {
            return new JsonObject { ["b64"] = Convert.ToBase64String(data) };
        }

        var meta = new JsonObject();
        foreach (var (key, value) in read.Metadata)
        {
            meta[key] = Kind(value);
        }

        if (meta["created"] is JsonObject { } created
            && created["k"]?.GetValue<string>() == "str"
            && created["v"]!.GetValue<string>() is var stamp
            && NowCreated().IsMatch(stamp)
            && stamp != FixedCreated)
        {
            created["v"] = "<now>";
        }

        return new JsonObject { ["meta"] = meta, ["body"] = Frontmatter.Content(text) };
    }

    private static JsonObject Kind(PyValue value) => value switch
    {
        PyNone => new() { ["k"] = "null" },
        PyStr text => new() { ["k"] = "str", ["v"] = text.Text },
        PyList list => new() { ["k"] = "list", ["items"] = new JsonArray([.. list.Items.Select(i => i is PyStr s ? JsonValue.Create(s.Text) : null)]) },
        PyDict => new() { ["k"] = "dict" },
        PyScalar scalar => new() { ["k"] = "scalar", ["v"] = scalar.Rendered },
        _ => new() { ["k"] = "other" },
    };

    private static List<string> Strings(JsonNode node) => [.. node.AsArray().Select(n => n!.GetValue<string>())];

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

    [GeneratedRegex(@"^20\d\d-\d\d-\d\d \d\d:\d\d$")]
    private static partial Regex NowCreated();

    private sealed class FixedClock : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1700000123);
    }
}
