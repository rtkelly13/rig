using System.Text.Json.Nodes;
using Rig.Core.Governance;

namespace Rig.Core.Tests.Governance;

/// <summary><see cref="CheckNames"/>: the lexicon pinned to cases generated from the Python it was ported from, and drift over temp repos.</summary>
public sealed class CheckNamesTests : IDisposable
{
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private string Repo => _root.Folder("app");

    private static RepoPolicies Requiring(params string[] checks) =>
        RepoPolicies.Parse(new JsonObject { ["defaults"] = new JsonObject { ["required_checks"] = new JsonArray([.. checks.Select(c => JsonValue.Create(c))]) } }, "policy.json");

    private void Workflow(string file, string content) => File.WriteAllText(_root.File($"app/.github/workflows/{file}"), content);

    [Theory]
    [InlineData("ci", "ci", "")]
    [InlineData("CI", "ci", "not all lowercase | not in the preferred lexicon — did you mean 'ci'?")]
    [InlineData("Build & Test", "build", "not all lowercase | not snake_case — separators/punctuation ' ' '&' | not in the preferred lexicon — did you mean 'build'?")]
    [InlineData("Storybook & Visual Regression", "visual", "not all lowercase | not snake_case — separators/punctuation ' ' '&' | 3 words, at most 2 allowed | not in the preferred lexicon — did you mean 'visual'?")]
    [InlineData("Tests & Coverage", "coverage", "not all lowercase | not snake_case — separators/punctuation ' ' '&' | not in the preferred lexicon — did you mean 'coverage'?")]
    [InlineData("pr title", "pr_title", "not snake_case — separators/punctuation ' ' | not in the preferred lexicon — did you mean 'pr_title'?")]
    [InlineData("pr_checks", "pr_checks", "")]
    [InlineData("PR-Title", "pr_title", "not all lowercase | not snake_case — separators/punctuation '-' | not in the preferred lexicon — did you mean 'pr_title'?")]
    [InlineData("tset", null, "not in the preferred lexicon (allowed, but prefer a listed name)")]
    [InlineData("lnt", "lint", "not in the preferred lexicon — did you mean 'lint'?")]
    [InlineData("buid", "build", "not in the preferred lexicon — did you mean 'build'?")]
    [InlineData("deploy_prod", "deploy", "not in the preferred lexicon — did you mean 'deploy'?")]
    [InlineData("build_and_test_everything", "build", "4 words, at most 2 allowed | not in the preferred lexicon — did you mean 'build'?")]
    [InlineData("unit-tests", "unit", "not snake_case — separators/punctuation '-' | not in the preferred lexicon — did you mean 'unit'?")]
    [InlineData("my check", null, "not snake_case — separators/punctuation ' '")]
    [InlineData("x", null, "not in the preferred lexicon (allowed, but prefer a listed name)")]
    [InlineData("1abc", null, "does not match lowercase snake_case of at most two words")]
    [InlineData("a__b", null, "does not match lowercase snake_case of at most two words")]
    [InlineData("café", null, "does not match lowercase snake_case of at most two words")]
    [InlineData("it's", null, "not snake_case — separators/punctuation \"'\"")]
    [InlineData("Lint (ruff)", "lint", "not all lowercase | not snake_case — separators/punctuation ' ' '(' ')' | not in the preferred lexicon — did you mean 'lint'?")]
    [InlineData("typecheks", "typecheck", "not in the preferred lexicon — did you mean 'typecheck'?")]
    [InlineData("e2e-tests", "e2e", "not snake_case — separators/punctuation '-' | not in the preferred lexicon — did you mean 'e2e'?")]
    [InlineData("securty", "security", "not in the preferred lexicon — did you mean 'security'?")]
    [InlineData("benchmarks", "benchmark", "not in the preferred lexicon — did you mean 'benchmark'?")]
    [InlineData("doc", "docs", "not in the preferred lexicon — did you mean 'docs'?")]
    [InlineData("releases", "release", "not in the preferred lexicon — did you mean 'release'?")]
    [InlineData("formatting", null, "not in the preferred lexicon (allowed, but prefer a listed name)")]
    [InlineData("foo_bar", null, "not in the preferred lexicon (allowed, but prefer a listed name)")]
    [InlineData("foo", null, "not in the preferred lexicon (allowed, but prefer a listed name)")]
    [InlineData("--", null, "not snake_case — separators/punctuation '-'")]
    [InlineData("Ünit", "unit", "not all lowercase | not in the preferred lexicon — did you mean 'unit'?")]
    [InlineData("test-all", "test", "not snake_case — separators/punctuation '-' | not in the preferred lexicon — did you mean 'test'?")]
    [InlineData("publsh", "publish", "not in the preferred lexicon — did you mean 'publish'?")]
    [InlineData("licence", "licenses", "not in the preferred lexicon — did you mean 'licenses'?")]
    [InlineData("contrast_check", "contrast", "not in the preferred lexicon — did you mean 'contrast'?")]
    [InlineData("a-b-c", null, "not snake_case — separators/punctuation '-' | 3 words, at most 2 allowed")]
    [InlineData("tokenz", "tokens", "not in the preferred lexicon — did you mean 'tokens'?")]
    [InlineData("smoke tests", "smoke", "not snake_case — separators/punctuation ' ' | not in the preferred lexicon — did you mean 'smoke'?")]
    [InlineData("coverages", "coverage", "not in the preferred lexicon — did you mean 'coverage'?")]
    [InlineData("a\tb", null, "not snake_case — separators/punctuation '\\t'")]
    [InlineData("deps!", "deps", "not snake_case — separators/punctuation '!' | not in the preferred lexicon — did you mean 'deps'?")]
    [InlineData("migrate db", "migrate", "not snake_case — separators/punctuation ' ' | not in the preferred lexicon — did you mean 'migrate'?")]
    [InlineData("\"q\"", null, "not snake_case — separators/punctuation '\"'")]
    public void Names_lint_and_suggest_as_python_does(string name, string? suggestion, string warnings)
    {
        Assert.Equal(suggestion, CheckNames.SuggestPreferredName(name));
        Assert.Equal(warnings, string.Join(" | ", CheckNames.LintCheckName(name)));
    }

    [Fact]
    public void The_lexicon_keeps_its_order()
    {
        Assert.Equal(32, CheckNames.PreferredCheckNames.Count);
        Assert.Equal(("ci", "migrate"), (CheckNames.PreferredCheckNames[0].Key, CheckNames.PreferredCheckNames[^1].Key));
    }

    [Fact]
    public void A_repo_without_pull_request_workflows_is_skipped()
    {
        Workflow("release.yml", "on: push\njobs:\n  build: {}\n");

        var result = CheckNames.Inspect(Repo, "acme/app", Requiring("ci"));

        Assert.Equal(("no_pr_workflows", true, true), (result.Status, result.Registered, result.IsAligned));
        Assert.Empty(result.Declared);
    }

    [Fact]
    public void A_required_name_no_job_declares_is_drift()
    {
        Workflow("ci.yml", "on: [pull_request]\njobs:\n  lint: {}\n  tests:\n    name: Build & Test\n");

        var result = CheckNames.Inspect(Repo, "acme/app", Requiring("ci", "Build & Test"));

        Assert.Equal(("drift", false), (result.Status, result.IsAligned));
        Assert.Equal(["ci"], result.Missing);
        Assert.Equal(["Build & Test", "lint"], result.Declared.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("ci.yml :: tests", result.Declared["Build & Test"]);
        Assert.Equal(["Build & Test"], result.Warnings.Select(w => w.Key));
    }

    [Fact]
    public void Shape_warnings_alone_warn_and_lexicon_names_align()
    {
        Workflow("ci.yml", "on:\n  pull_request:\njobs:\n  ci: {}\n  my_check: {}\n");

        Assert.Equal("warn", CheckNames.Inspect(Repo, "acme/app", Requiring("ci", "my_check")).Status);
        Assert.Equal(("aligned", true), (CheckNames.Inspect(Repo, "acme/app", Requiring("ci")).Status, CheckNames.Inspect(Repo, "acme/app", Requiring("ci")).Registered));
        Assert.Equal(("aligned", false), (CheckNames.Inspect(Repo, "acme/app", Requiring()).Status, CheckNames.Inspect(Repo, "acme/app", Requiring()).Registered));
    }
}
