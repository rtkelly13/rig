using System.Text.Json.Nodes;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// Holds the C# vault lint to <c>rig.obsidian.lint_manager.VaultLinter</c>. <c>lint-oracle.json</c> records synthetic vaults
/// (every metadata, filename and duplicate rule, refused notes, dot-folders, a folder named <c>*.md</c>, and on POSIX names
/// Windows cannot hold and symlinks) with the Python's JSON report, per-folder stats and vault stats. The Python visited
/// notes in directory order, so metadata findings are compared as a multiset. Set <c>RIG_LINT_ORACLE</c> to a fixture of
/// the same shape whose cases name a <c>vault</c> folder instead of listing files, such as one recorded from a real vault.
/// </summary>
public sealed class VaultLintOracleTests
{
    private const string ExtraFixtureVariable = "RIG_LINT_ORACLE";

    [Fact]
    public void Lints_every_recorded_vault_as_python_did()
    {
        var cases = Load(Path.Combine(AppContext.BaseDirectory, "Vault", "lint-oracle.json")).ToList();
        var extra = Environment.GetEnvironmentVariable(ExtraFixtureVariable);
        if (!string.IsNullOrEmpty(extra))
        {
            cases.AddRange(Load(extra));
        }

        var mismatches = new List<string>();
        foreach (var oracle in cases.Where(c => !(c.PosixOnly && OperatingSystem.IsWindows())))
        {
            if (oracle.Vault is { } vault)
            {
                mismatches.AddRange(Compare(oracle, vault));
                continue;
            }

            var root = Path.Combine(RepoTemp.NewVaultParent("rig-lint"), "Vault");
            try
            {
                Materialize(root, oracle.Files);
                mismatches.AddRange(Compare(oracle, root));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} difference(s) from the Python:\n{string.Join('\n', mismatches.Take(40))}");
    }

    [Fact]
    public void A_note_whose_source_is_malformed_is_passed_over_not_fatal()
    {
        // The Python's filename check let urlsplit's ValueError escape and the lint crashed; C# logs the note and moves on.
        using var vault = new TempVault();
        vault.Write("Resources/Topic/bad_source.md", "---\ntype: resource\nsource: http://[::1\nstatus: x\ncreated: y\n---\n");
        vault.Write("Resources/Topic/other_note.md", "---\ntype: resource\nsource: https://example.org/a\nstatus: x\ncreated: y\n---\n");
        vault.Write("Resources/Topic/refused_note.md", "---\ntype: [unclosed\n---\n");
        var logged = new List<string>();
        var linter = new VaultLinter(vault.Root, logged.Add);

        var issues = linter.FindFilenameIssues();
        var duplicates = linter.FindDuplicates();

        Assert.All(issues, issue => Assert.Equal("Resources/Topic/other_note.md", issue.Note.Relative.ToString()));
        Assert.NotEmpty(issues);
        Assert.Empty(duplicates);
        Assert.Equal(2, logged.Count(line => line.Contains("bad_source.md", StringComparison.Ordinal)));
        Assert.Equal(2, logged.Count(line => line.Contains("refused_note.md", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_clean_vault_reports_ok()
    {
        using var vault = new TempVault();
        vault.Write("Resources/Topic/Clean Note.md", "---\ntype: resource\nsource: https://example.org/a\nstatus: x\ncreated: y\n---\n");

        var report = new VaultLinter(vault.Root).Report();

        Assert.True(report.Ok);
        Assert.StartsWith("{\n  \"ok\": true,", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_vault_has_no_notes() =>
        Assert.Empty(VaultWalk.Markdown(Path.Combine(Path.GetTempPath(), $"rig-no-vault-{Guid.NewGuid():N}")));

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void An_unreadable_folder_is_passed_over()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        using var vault = new TempVault();
        vault.Write("Resources/Open/a.md", "a");
        vault.Write("Resources/Shut/b.md", "b");
        var shut = Path.Combine(vault.Root, "Resources", "Shut");
        File.SetUnixFileMode(shut, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(Listable(shut), "File modes do not restrict this user (root).");
            Assert.Equal(["Resources/Open/a.md"], VaultWalk.Markdown(vault.Root).Select(e => e.Relative.ToString()));
        }
        finally
        {
            File.SetUnixFileMode(shut, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Code_point_order_puts_supplementary_characters_after_the_private_use_area()
    {
        string[] sorted = ["b", "\uE000", "\U0001F600", null!, "a", "ab"];
        Array.Sort(sorted, PyText.CodePointOrder);

        Assert.Equal([null!, "a", "ab", "b", "\uE000", "\U0001F600"], sorted);
    }

    [Fact]
    public void Vault_stats_count_an_inner_vault_folder_when_there_is_one()
    {
        using var vault = new TempVault();
        vault.Write("Vault/Resources/a.md", "a");
        vault.Write("Vault/Resources/b.md", "b");
        vault.Write("Elsewhere/c.md", "c");

        Assert.Equal([new("Resources", 2), new("Total", 2)], VaultLinter.VaultStats(vault.Root));
    }

    [Fact]
    public void Vault_stats_of_a_missing_vault_are_an_empty_total() =>
        Assert.Equal([new("Total", 0)], VaultLinter.VaultStats(Path.Combine(Path.GetTempPath(), $"rig-no-vault-{Guid.NewGuid():N}")));

    [Fact]
    public void Json_is_written_as_python_json_dumps_writes_it()
    {
        var report = new LintReport(
            false,
            "q\"\\\n\r\t\b\f\u0001\u001fé😀",
            [],
            [new("a.md", "TVS005", "Unknown note type: 'x'", "warning")],
            [new("b_c.md", "warning", "filename uses inconsistent separators", "b-c.md", ["TVS103"])],
            []);

        Assert.Equal(
            """
            {
              "ok": false,
              "vault_path": "q\"\\\n\r\t\b\f\u0001\u001fé😀",
              "metadata": {
                "errors": [],
                "warnings": [
                  {
                    "path": "a.md",
                    "code": "TVS005",
                    "reason": "Unknown note type: 'x'",
                    "severity": "warning"
                  }
                ]
              },
              "filenames": [
                {
                  "path": "b_c.md",
                  "severity": "warning",
                  "reason": "filename uses inconsistent separators",
                  "suggested_path": "b-c.md",
                  "codes": [
                    "TVS103"
                  ]
                }
              ],
              "duplicates": {}
            }
            """.ReplaceLineEndings("\n"),
            report.ToJson());
    }

    private static IEnumerable<string> Compare(OracleCase oracle, string vault)
    {
        var linter = new VaultLinter(vault);
        var report = JsonNode.Parse(linter.Report().ToJson())!.AsObject();
        report.Remove("vault_path");
        var expected = oracle.Expected["report"]!.AsObject();
        var name = oracle.Name;

        if (report["ok"]!.GetValue<bool>() != expected["ok"]!.GetValue<bool>())
        {
            yield return $"{name}: ok is {report["ok"]}, Python gave {expected["ok"]}";
        }

        foreach (var group in new[] { "errors", "warnings" })
        {
            var actual = Multiset(report["metadata"]![group]!.AsArray());
            var wanted = Multiset(expected["metadata"]![group]!.AsArray());
            foreach (var extra in actual.Except(wanted))
            {
                yield return $"{name}: extra metadata {group[..^1]} {extra}";
            }

            foreach (var missing in wanted.Except(actual))
            {
                yield return $"{name}: missing metadata {group[..^1]} {missing}";
            }
        }

        foreach (var difference in Differences($"{name}: filenames", report["filenames"], expected["filenames"]))
        {
            yield return difference;
        }

        foreach (var difference in KeyedDifferences($"{name}: duplicates", report["duplicates"]!.AsObject(), expected["duplicates"]!.AsObject()))
        {
            yield return difference;
        }

        var folders = new JsonObject();
        foreach (var (key, stats) in linter.FolderStats())
        {
            folders[key] = new JsonObject
            {
                ["metadata_errors"] = stats.MetadataErrors,
                ["metadata_warnings"] = stats.MetadataWarnings,
                ["filename_issues"] = stats.FilenameIssues,
                ["duplicates"] = stats.Duplicates,
                ["total_notes"] = stats.TotalNotes,
                ["score"] = stats.Score,
            };
        }

        foreach (var difference in KeyedDifferences($"{name}: folders", folders, oracle.Expected["folders"]!.AsObject()))
        {
            yield return difference;
        }

        var health = new JsonObject();
        foreach (var (key, count) in VaultLinter.VaultStats(vault))
        {
            health[key] = count;
        }

        var wantedHealth = oracle.Expected["health"]!.AsObject();
        if (health.ToJsonString() != wantedHealth.ToJsonString())
        {
            yield return $"{name}: vault stats {health.ToJsonString()}, Python gave {wantedHealth.ToJsonString()}";
        }
    }

    // The Python's TVS000 reasons carry its own exception text, which the oracle drops.
    private static List<string> Multiset(JsonArray items) =>
    [
        .. items.Select(item =>
        {
            var copy = item!.DeepClone().AsObject();
            if (copy["code"]!.GetValue<string>() == LintCodes.InvalidFrontmatter)
            {
                copy["reason"] = null;
            }

            return copy.ToJsonString();
        }).Order(StringComparer.Ordinal),
    ];

    private static IEnumerable<string> Differences(string label, JsonNode? actual, JsonNode? expected)
    {
        if (!JsonNode.DeepEquals(actual, expected))
        {
            yield return $"{label}: {actual?.ToJsonString()}, Python gave {expected?.ToJsonString()}";
        }
    }

    private static IEnumerable<string> KeyedDifferences(string label, JsonObject actual, JsonObject expected)
    {
        foreach (var (key, value) in actual)
        {
            if (!expected.TryGetPropertyValue(key, out var wanted))
            {
                yield return $"{label}: extra {key} = {value?.ToJsonString()}";
            }
            else if (!JsonNode.DeepEquals(value, wanted))
            {
                yield return $"{label}: {key} = {value?.ToJsonString()}, Python gave {wanted?.ToJsonString()}";
            }
        }

        foreach (var (key, value) in expected.Where(pair => !actual.ContainsKey(pair.Key)))
        {
            yield return $"{label}: missing {key} = {value?.ToJsonString()}";
        }
    }

    internal static void Materialize(string root, JsonArray files)
    {
        Directory.CreateDirectory(root);
        foreach (var node in files)
        {
            var entry = node!.AsObject();
            var full = Path.Combine(root, entry["path"]!.GetValue<string>());
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (entry["link"] is { } target)
            {
                File.CreateSymbolicLink(full, target.GetValue<string>());
            }
            else if (entry["folder"] is not null)
            {
                Directory.CreateDirectory(full);
            }
            else
            {
                File.WriteAllBytes(full, Convert.FromBase64String(entry["b64"]!.GetValue<string>()));
            }
        }
    }

    private static IEnumerable<OracleCase> Load(string path)
    {
        foreach (var node in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
        {
            var item = node!.AsObject();
            yield return new OracleCase(
                item["name"]!.GetValue<string>(),
                item["posix_only"]?.GetValue<bool>() ?? false,
                item["files"]?.AsArray() ?? [],
                item["vault"]?.GetValue<string>(),
                item["expect"]!.AsObject());
        }
    }

    private sealed record OracleCase(string Name, bool PosixOnly, JsonArray Files, string? Vault, JsonObject Expected);

    private static bool Listable(string folder)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(folder);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class TempVault : IDisposable
    {
        private readonly string parent = RepoTemp.NewVaultParent("rig-lint");

        public TempVault()
        {
            Root = Path.Combine(parent, "Vault");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relative, string text)
        {
            var full = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public void Dispose() => Directory.Delete(parent, recursive: true);
    }
}
