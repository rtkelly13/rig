using System.Text.RegularExpressions;
using Rig.Core.Governance;

namespace Rig.Core.Tests.Governance;

/// <summary>
/// The tables <see cref="GitignoreEntry"/> and <see cref="BespokeAgentFiles"/> copied from Python, pinned against the
/// Python the repo-governance audits still run. Delete this with <c>src/rig/repo_governance</c>.
/// </summary>
public sealed partial class PythonParityContractTests
{
    private static string Python(string relative) =>
        File.ReadAllText(Path.Join(Path.GetDirectoryName(RepoTemp.Root), "src", "rig", "repo_governance", relative));

    [Fact]
    public void Every_python_synonym_satisfies_its_token()
    {
        var block = SynonymsBlock().Match(Python("policy_config.py"));
        Assert.True(block.Success, "policy_config.py no longer has its synonyms table");

        var pairs = SynonymLine().Matches(block.Groups["body"].Value)
            .SelectMany(line => Quoted().Matches(line.Groups["forms"].Value).Select(form => (Token: line.Groups["token"].Value, Form: form.Groups[1].Value)))
            .ToList();

        Assert.Equal(7, pairs.Select(p => p.Token).Distinct().Count());
        Assert.All(pairs, pair => Assert.Empty(new GitignoreEntry("e", "c", [pair.Token], [pair.Token]).MissingLines(pair.Form + "\n")));
    }

    [Fact]
    public void The_bespoke_files_and_comment_match_python()
    {
        var python = Python("agent_files.py");
        var tuple = BespokeTuple().Match(python);
        Assert.True(tuple.Success, "agent_files.py no longer has BESPOKE_AGENT_FILES");

        Assert.Equal(Quoted().Matches(tuple.Groups["body"].Value).Select(m => m.Groups[1].Value), BespokeAgentFiles.Defaults);
        Assert.Contains($"\"{BespokeAgentFiles.Comment}\"", python, StringComparison.Ordinal);
        Assert.Contains($"GITIGNORE_ENTRY_NAME = \"{BespokeAgentFiles.EntryName}\"", python, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"synonyms = \{(?<body>.*?)\n    \}", RegexOptions.Singleline)]
    private static partial Regex SynonymsBlock();

    [GeneratedRegex(@"^\s*""(?<token>[^""]+)"": \{(?<forms>[^}]*)\},", RegexOptions.Multiline)]
    private static partial Regex SynonymLine();

    [GeneratedRegex(@"BESPOKE_AGENT_FILES: tuple\[str, \.\.\.\] = \((?<body>.*?)\n\)", RegexOptions.Singleline)]
    private static partial Regex BespokeTuple();

    [GeneratedRegex(@"""([^""]*)""")]
    private static partial Regex Quoted();
}
