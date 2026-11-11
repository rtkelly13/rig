namespace Rig.Core.Governance;

/// <summary>
/// One required <c>.gitignore</c> rule or rule group from <c>config/repo_policies.json</c>. Ported from
/// <c>rig.repo_governance.policy_config.GitignoreEntry</c>.
/// </summary>
/// <param name="Name">What it is called, so a repo override can replace it.</param>
/// <param name="Comment">The comment written above its lines.</param>
/// <param name="Write">The lines appended when it is missing.</param>
/// <param name="SatisfiedBy">The lines any one of which satisfies it, when <paramref name="MatchAll"/> is false.</param>
/// <param name="MatchAll">True when every line of <paramref name="Write"/> must be satisfied on its own.</param>
public sealed record GitignoreEntry(string Name, string Comment, IReadOnlyList<string> Write, IReadOnlyList<string> SatisfiedBy, bool MatchAll = false)
{
    // Lines that satisfy a token the way people commonly write it, beyond its slash variants.
    private static readonly string[] TempForms = ["temp", "temp/", "/temp", "/temp/", ".temp", ".temp/", "/.temp", "/.temp/"];
    private static readonly string[] VenvForms = [".venv", ".venv/", "venv", "venv/", "env", "env/", "ENV", "ENV/"];

    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
    {
        ["temp"] = TempForms,
        [".temp"] = TempForms,
        ["bin"] = ["[Bb]in", "[Bb]in/", "**/bin", "**/bin/", "/bin/"],
        ["obj"] = ["[Oo]bj", "[Oo]bj/", "**/obj", "**/obj/", "/obj/"],
        ["venv"] = VenvForms,
        [".venv"] = VenvForms,
        ["node_modules"] = ["node_modules", "node_modules/", "/node_modules", "/node_modules/", "**/node_modules"],
    };

    private static readonly string[] DsStoreForms = [".DS_Store", ".DS_Store*", "**/.DS_Store", "*/.DS_Store"];

    // Python's str.splitlines separators, \r\n counted once.
    private static readonly string[] LineBreaks = ["\r\n", "\n", "\r", "\v", "\f", "\x1c", "\x1d", "\x1e", "\x85", "\u2028", "\u2029"];

    /// <summary>The lines of <see cref="Write"/> that <paramref name="gitignoreText"/> does not satisfy.</summary>
    public IReadOnlyList<string> MissingLines(string gitignoreText)
    {
        ArgumentNullException.ThrowIfNull(gitignoreText);
        var lines = gitignoreText.Split(LineBreaks, StringSplitOptions.None)
            .Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (MatchAll)
        {
            return [.. Write.Where(item => !IsSatisfied(item, lines))];
        }

        return SatisfiedBy.Any(token => IsSatisfied(token, lines)) ? [] : Write;
    }

    /// <summary>The block appended for <paramref name="missing"/>: a blank line, the comment, then the lines.</summary>
    public string RenderMissing(IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        return missing.Count == 0 ? string.Empty : $"\n# {Comment}\n{string.Join('\n', missing)}\n";
    }

    private static bool IsSatisfied(string token, HashSet<string> lines)
    {
        if (lines.Contains(token))
        {
            return true;
        }

        var stripped = token.Trim('/');
        if (stripped.Length > 0
            && new[] { stripped, $"{stripped}/", $"/{stripped}", $"/{stripped}/", $"**/{stripped}", $"**/{stripped}/" }.Any(lines.Contains))
        {
            return true;
        }

        if (Synonyms.TryGetValue(stripped, out var forms) && forms.Any(lines.Contains))
        {
            return true;
        }

        if (token is "TODO.md" or "todo.md" && lines.Contains("[Tt][Oo][Dd][Oo].md"))
        {
            return true;
        }

        return token == ".DS_Store" && DsStoreForms.Any(lines.Contains);
    }
}
