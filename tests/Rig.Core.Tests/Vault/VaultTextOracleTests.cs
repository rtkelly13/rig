using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// Holds the C# vault text rules to what the Python they were ported from returned. <c>vault-text-oracle.json</c> records
/// each Python helper's result, or the <c>ValueError</c> it raised, for a corpus of URLs, stems, titles and paths built to
/// hit the edges: Python's whitespace and casing, <c>urlsplit</c>'s failures, <c>parse_qs</c> grouping, code-point
/// truncation and the candidate scoring. Set <c>RIG_VAULT_ORACLE</c> to a second fixture of the same shape, such as one
/// recorded from a real vault, to check that too.
/// </summary>
public sealed class VaultTextOracleTests
{
    private const string ExtraFixtureVariable = "RIG_VAULT_ORACLE";

    private static readonly Lazy<List<OracleCase>> Cases = new(LoadAll);

    public static TheoryData<string> Functions() => [.. Cases.Value.Select(c => c.Fn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Functions))]
    public void Matches_the_python_for_every_recorded_case(string fn)
    {
        var mismatches = Cases.Value
            .Where(c => c.Fn == fn)
            .Select(c => (Case: c, Actual: Run(c)))
            .Where(pair => !Same(pair.Case.Expected, pair.Actual))
            .Select(pair => $"{pair.Case.Args.ToJsonString()} -> {pair.Actual.ToJsonString()}, Python gave {pair.Case.Expected.ToJsonString()}")
            .ToList();

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} case(s) differ from the Python:\n{string.Join('\n', mismatches.Take(40))}");
    }

    [Fact]
    public void The_fixture_covers_every_ported_function()
    {
        string[] expected =
        [
            "clean_url", "clean_ws", "filename_issue_specs", "is_http_url", "looks_like_url", "looks_like_urlish_stem",
            "needs_filename_cleanup", "normalize_url", "portable_windows_stem", "rescore", "sanitize_filename",
            "slugify_note_stem", "suggest_clean_filename_stem", "suggested_type", "windows_portability_issue_reason",
        ];

        Assert.Equal(expected, Functions().Select(row => (string)row.Data));
    }

    [Fact]
    public void Folder_stats_score_100_before_rescoring()
    {
        Assert.Equal(100.0, new FolderLintStats { TotalNotes = 3, MetadataErrors = 3 }.Score);
    }

    private static JsonObject Run(OracleCase c)
    {
        try
        {
            return Dispatch(c.Fn, c.Args);
        }
        catch (FormatException)
        {
            return new JsonObject { ["error"] = "ValueError" };
        }
    }

    private static JsonObject Dispatch(string fn, JsonArray args)
    {
        string S(int i) => args[i]!.GetValue<string>();
        int I(int i) => args[i]!.GetValue<int>();
        NotePath? P(int i) => args[i] is null ? null : NotePath.Parse(S(i));

        return fn switch
        {
            "clean_ws" => Result(VaultText.CleanWhitespace(S(0))),
            "normalize_url" => Result(VaultText.NormalizeUrl(S(0))),
            "is_http_url" => Result(VaultText.IsHttpUrl(S(0))),
            "clean_url" => Result(VaultText.CleanUrl(S(0))),
            "sanitize_filename" => Result(VaultText.SanitizeFilename(S(0), I(1))),
            "slugify_note_stem" => Result(VaultText.SlugifyNoteStem(S(0), I(1))),
            "looks_like_url" => Result(VaultText.LooksLikeUrl(S(0))),
            "looks_like_urlish_stem" => Result(VaultText.LooksLikeUrlishStem(S(0))),
            "suggest_clean_filename_stem" => Result(FilenameStem.Suggest(S(0), S(1), S(2), S(3), I(4))),
            "portable_windows_stem" => Result(LintRules.PortableWindowsStem(S(0))),
            "needs_filename_cleanup" => Result(LintRules.NeedsFilenameCleanup(S(0), P(1))),
            "filename_issue_specs" => Result(new JsonArray([.. LintRules.FilenameIssueSpecs(S(0), S(1), P(2))
                .Select(spec => (JsonNode)new JsonArray(spec.Code, spec.Reason, spec.Severity))])),
            "windows_portability_issue_reason" => Result(LintRules.WindowsPortabilityIssueReason(NotePath.Parse(S(0)))),
            "suggested_type" => Result(LintRules.SuggestedType(NotePath.Parse(S(0)))),
            "rescore" => Result(Rescore(I(0), I(1), I(2), I(3), I(4))),
            _ => throw new InvalidOperationException($"The fixture records {fn}, which this test does not know."),
        };
    }

    private static double Rescore(int errors, int warnings, int filenames, int duplicates, int total)
    {
        var stats = new FolderLintStats
        {
            MetadataErrors = errors,
            MetadataWarnings = warnings,
            FilenameIssues = filenames,
            Duplicates = duplicates,
            TotalNotes = total,
        };
        stats.Rescore();
        return stats.Score;
    }

    private static JsonObject Result(JsonNode? value) => new() { ["result"] = value };

    private static JsonObject Result(string? value) => Result(value is null ? null : JsonValue.Create(value));

    private static JsonObject Result(bool value) => Result(JsonValue.Create(value));

    private static JsonObject Result(double value) => Result(JsonValue.Create(value));

    // Numbers compare by value, as Python writes 100.0 and System.Text.Json may write 100.
    private static bool Same(JsonNode expected, JsonNode actual)
    {
        if (expected["result"] is JsonValue e && actual["result"] is JsonValue a
            && e.GetValueKind() == JsonValueKind.Number && a.GetValueKind() == JsonValueKind.Number)
        {
            return e.GetValue<double>().Equals(a.GetValue<double>());
        }

        return JsonNode.DeepEquals(expected, actual);
    }

    private static List<OracleCase> LoadAll()
    {
        var cases = Load(Path.Combine(AppContext.BaseDirectory, "Vault", "vault-text-oracle.json")).ToList();
        var extra = Environment.GetEnvironmentVariable(ExtraFixtureVariable);
        if (!string.IsNullOrEmpty(extra))
        {
            cases.AddRange(Load(extra));
        }

        return cases;
    }

    private static IEnumerable<OracleCase> Load(string path)
    {
        foreach (var node in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
        {
            var item = node!.AsObject();
            var expected = item.ContainsKey("error")
                ? new JsonObject { ["error"] = item["error"]!.DeepClone() }
                : new JsonObject { ["result"] = item["result"]?.DeepClone() };
            yield return new OracleCase(item["fn"]!.GetValue<string>(), item["args"]!.AsArray(), expected);
        }
    }

    private sealed record OracleCase(string Fn, JsonArray Args, JsonObject Expected);
}
