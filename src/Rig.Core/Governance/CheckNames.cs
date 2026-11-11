using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rig.Core.Vault;

namespace Rig.Core.Governance;

/// <summary>
/// The check names a repo's workflows report on a pull request, read off the YAML on disk. A required status check is a
/// contract between the workflows and the ruleset, and nothing in GitHub connects the two halves; see
/// docs/repo-governance/required-checks.md. Ported from <c>rig.repo_governance.check_names</c>.
/// </summary>
public static partial class CheckNames
{
    /// <summary>The most words a check name may have.</summary>
    public const int MaxCheckNameWords = 2;

    /// <summary>
    /// The shared lexicon, in order: one canonical name per common developer task, so the same job answers to the same
    /// string in every repo. Add to it rather than inventing a synonym.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> PreferredCheckNames { get; } =
    [
        new("ci", "Aggregate gate — passes only if every other job passed"),
        new("pr-checks", "Pull request validation suite (common aggregate name)"),
        new("pr_checks", "Pull request validation suite"),
        new("build", "Compile, bundle or package the artefact"),
        new("typecheck", "Static type checking (tsc, pyright, mypy)"),
        new("codegen", "Generated output still matches the source it is generated from"),
        new("test", "The default test suite (unit tests, when there is only one suite)"),
        new("tests", "The default test suite (common plural alias for test)"),
        new("unit", "Unit tests, when split out from other suites"),
        new("e2e", "End-to-end tests through the real interface"),
        new("visual", "Visual regression / screenshot comparison"),
        new("smoke", "Post-deploy sanity check against a running deployment"),
        new("regression", "Full suite before merge — a commit status, pending until the PR is labelled ready-to-merge"),
        new("bench", "Performance benchmarks"),
        new("benchmark", "Performance benchmarks (common alias for bench)"),
        new("coverage", "Coverage thresholds and reporting"),
        new("lint", "Static analysis and style rules"),
        new("format", "Formatting check (prettier, ruff format, dotnet format)"),
        new("a11y", "Accessibility audit"),
        new("contrast", "Colour contrast of token pairs"),
        new("tokens", "Design token drift — generated CSS matches its source"),
        new("deps", "Dependency hygiene — unused, unlisted, misplaced, unpinned"),
        new("security", "Vulnerability, secret and SAST scanning"),
        new("licenses", "Licence policy compliance"),
        new("action_shas", "GitHub Actions pinned to 40-character commit SHAs"),
        new("docs", "Documentation build or link check"),
        new("pr_title", "Pull request title convention (Conventional Commits)"),
        new("pr-title", "Pull request title convention (hyphenated alias)"),
        new("publish", "Publish a package or image to a registry"),
        new("release", "Cut a release — tag, changelog, artefacts"),
        new("deploy", "Deploy to an environment"),
        new("migrate", "Database migration check"),
    ];

    private static readonly HashSet<string> Lexicon = [.. PreferredCheckNames.Select(pair => pair.Key)];

    /// <summary>Warnings about a check name's shape; empty when it conforms.</summary>
    public static IReadOnlyList<string> LintCheckName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var warnings = new List<string>();
        if (Lexicon.Contains(name))
        {
            return warnings;
        }

        if (!string.Equals(name, name.ToLowerInvariant(), StringComparison.Ordinal))
        {
            warnings.Add("not all lowercase");
        }

        // Anything not alphanumeric or an underscore is a separator someone chose instead of snake_case.
        var stray = name.EnumerateRunes().Where(rune => !Rune.IsLetterOrDigit(rune) && rune.Value != '_').Distinct().Order().ToList();
        if (stray.Count > 0)
        {
            warnings.Add($"not snake_case — separators/punctuation {string.Join(' ', stray.Select(rune => PyRepr.Str(rune.ToString())))}");
        }

        var words = Words(name);
        if (words.Count > MaxCheckNameWords)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"{words.Count} words, at most {MaxCheckNameWords} allowed"));
        }

        if (warnings.Count == 0 && !CheckNameShape().IsMatch(name))
        {
            warnings.Add("does not match lowercase snake_case of at most two words");
        }

        if (SuggestPreferredName(name) is { } suggestion)
        {
            warnings.Add($"not in the preferred lexicon — did you mean {PyRepr.Str(suggestion)}?");
        }
        else if (warnings.Count == 0)
        {
            warnings.Add("not in the preferred lexicon (allowed, but prefer a listed name)");
        }

        return warnings;
    }

    /// <summary>
    /// The closest lexicon entry to a name, or null: matched on its words rather than the whole string, because the
    /// names this repairs are sentences (<c>Storybook &amp; Visual Regression</c> contains <c>visual</c>).
    /// </summary>
    public static string? SuggestPreferredName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var words = Words(name).Select(word => word.ToLowerInvariant()).ToList();
        if (words.Count == 0)
        {
            return null;
        }

        foreach (var joined in new[] { string.Join('_', words), string.Join('-', words) })
        {
            if (Lexicon.Contains(joined))
            {
                return joined;
            }
        }

        // Where both "tests" and "coverage" appear, prefer "coverage".
        if (words.Contains("coverage"))
        {
            return "coverage";
        }

        return words.FirstOrDefault(Lexicon.Contains) ?? words.Select(CloseMatch).FirstOrDefault(match => match is not null);
    }

    /// <summary>
    /// A repo's required check names against its workflows on disk (drift, an error) and against the lexicon (shape, a
    /// warning). See docs/repo-governance/required-checks.md.
    /// </summary>
    public static CheckNamesResult Inspect(string repoPath, string repo, RepoPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var required = policies.RequiredChecks(repo);
        var declared = DiscoverPrCheckNames(repoPath);
        if (declared.Count == 0)
        {
            return new("no_pr_workflows", required, declared, [], [], required.Count > 0, true);
        }

        IReadOnlyList<string> missing = [.. required.Where(name => !declared.ContainsKey(name))];
        var warnings = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        foreach (var name in required.Distinct())
        {
            if (LintCheckName(name) is { Count: > 0 } warns)
            {
                warnings.Add(new(name, warns));
            }
        }

        var status = missing.Count > 0 ? "drift" : warnings.Count > 0 ? "warn" : "aligned";
        return new(status, required, declared, missing, warnings, required.Count > 0, missing.Count == 0);
    }

    private static List<string> Words(string name) => [.. NonWord().Split(name).Where(word => word.Length > 0)];

    // difflib.get_close_matches(word, lexicon, n=1, cutoff=0.8): the best ratio, ties to the greater name.
    private static string? CloseMatch(string word)
    {
        const double cutoff = 0.8;
        string? best = null;
        var bestScore = 0.0;
        foreach (var candidate in PreferredCheckNames.Select(pair => pair.Key))
        {
            var score = 2.0 * Matched(candidate, 0, candidate.Length, word, 0, word.Length) / (candidate.Length + word.Length);
            if (score >= cutoff && (best is null || score > bestScore || (score == bestScore && string.CompareOrdinal(candidate, best) > 0)))
            {
                (best, bestScore) = (candidate, score);
            }
        }

        return best;
    }

    // The characters SequenceMatcher's matching blocks cover: the longest common run (earliest in a, then in b),
    // then the same on each side of it.
    private static int Matched(string a, int alo, int ahi, string b, int blo, int bhi)
    {
        var (besti, bestj, size) = (alo, blo, 0);
        var lengths = new int[bhi - blo + 1];
        for (var i = alo; i < ahi; i++)
        {
            var next = new int[bhi - blo + 1];
            for (var j = blo; j < bhi; j++)
            {
                if (a[i] != b[j])
                {
                    continue;
                }

                var k = next[j - blo + 1] = lengths[j - blo] + 1;
                if (k > size)
                {
                    (besti, bestj, size) = (i - k + 1, j - k + 1, k);
                }
            }

            lengths = next;
        }

        return size == 0
            ? 0
            : size + Matched(a, alo, besti, b, blo, bestj) + Matched(a, besti + size, ahi, b, bestj + size, bhi);
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[_-][a-z0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CheckNameShape();

    [GeneratedRegex("[^a-zA-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWord();

    /// <summary>
    /// <c>{check name: "workflow file :: job id"}</c> for every job in a workflow triggered by <c>pull_request</c>: a
    /// job's <c>name:</c>, falling back to its id. A workflow's <c>env.STATUS_CONTEXT</c> counts too, as
    /// <c>"workflow file :: status"</c>: a commit status it posts. A file that does not parse is skipped, not fatal.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DiscoverPrCheckNames(string repoPath)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var workflows = Path.Join(repoPath, ".github", "workflows");
        if (!Directory.Exists(workflows))
        {
            return found;
        }

        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(workflows).Where(file => IsWorkflowName(Path.GetFileName(file))).Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable workflows directory names no checks, as an absent one does.
            return found;
        }

        foreach (var file in files)
        {
            if (Load(file) is not { } workflow || Lookup(workflow, "jobs") is not PyDict jobs || !Triggers(workflow).Contains("pull_request"))
            {
                continue;
            }

            var fileName = Path.GetFileName(file);
            foreach (var (key, job) in jobs.Entries)
            {
                var jobId = KeyText(key);
                var name = job is PyDict body && Lookup(body, "name") is PyStr { Text: var named } ? named : jobId;
                _ = found.TryAdd(name, $"{fileName} :: {jobId}");
            }

            if (Lookup(workflow, "env") is PyDict env && Lookup(env, "STATUS_CONTEXT") is PyStr { Text: { Length: > 0 } status })
            {
                _ = found.TryAdd(status, $"{fileName} :: status");
            }
        }

        return found;
    }

    // `*.y*ml`, as pathlib's glob matches it.
    private static bool IsWorkflowName(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return false;
        }

        var extension = name[(dot + 1)..];
        return extension.StartsWith('y') && extension.EndsWith("ml", StringComparison.Ordinal) && extension.Length >= 3;
    }

    // The workflow's top-level mapping with string keys only, PyYAML's `on: → True` folded back; null when unreadable.
    private static PyDict? Load(string file)
    {
        PyValue? loaded;
        try
        {
            loaded = PyYaml.Load(File.ReadAllText(file));
        }
        catch (Exception e) when (e is PyYamlException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }

        if (loaded is not PyDict document)
        {
            return null;
        }

        var hasOn = document.Entries.Any(e => e.Key is PyStr { Text: "on" });
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var entry in document.Entries)
        {
            if (entry.Key is PyStr)
            {
                entries.Add(entry);
            }
            else if (entry.Key is PyScalar { Rendered: "True" } && !hasOn)
            {
                entries.Add(new(new PyStr("on"), entry.Value));
            }
        }

        return new PyDict(entries);
    }

    // The triggers a workflow's `on:` names: a mapping's keys, a list's items or a lone scalar.
    private static HashSet<string> Triggers(PyDict workflow) => Lookup(workflow, "on") switch
    {
        PyDict events => [.. events.Entries.Select(e => KeyText(e.Key))],
        PyList events => [.. events.Items.Select(KeyText)],
        PyNone or null => [],
        var single => [KeyText(single)],
    };

    // The last value under string key `key`, as a Python dict holds it.
    private static PyValue? Lookup(PyDict map, string key) =>
        map.Entries.LastOrDefault(e => e.Key is PyStr { Text: var text } && text == key).Value;

    private static string KeyText(PyValue value) => value switch
    {
        PyStr text => text.Text,
        PyScalar scalar => scalar.Rendered,
        PyNone => "None",
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>A repo's required check names against its workflows and the lexicon.</summary>
/// <param name="Status"><c>no_pr_workflows</c>, <c>drift</c>, <c>warn</c> or <c>aligned</c>.</param>
/// <param name="Required">The names the policy requires.</param>
/// <param name="Declared">The names <c>pull_request</c> jobs report, with where each is declared.</param>
/// <param name="Missing">Required names no job reports: drift.</param>
/// <param name="Warnings">Each required name with shape warnings, in order, and its warnings.</param>
/// <param name="Registered">Whether the policy requires any checks of the repo.</param>
/// <param name="IsAligned">Whether nothing is missing.</param>
public sealed record CheckNamesResult(
    string Status,
    IReadOnlyList<string> Required,
    IReadOnlyDictionary<string, string> Declared,
    IReadOnlyList<string> Missing,
    IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Warnings,
    bool Registered,
    bool IsAligned);
