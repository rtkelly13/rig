using System.Text.RegularExpressions;

namespace Rig.Core.Governance;

/// <summary>Which community templates a repo has.</summary>
/// <param name="RepoName">The clone's folder name.</param>
/// <param name="TotalTemplates">How many templates are governed.</param>
/// <param name="Missing">The repo-relative paths absent.</param>
/// <param name="Present">The repo-relative paths present.</param>
public sealed record OssTemplatesAudit(string RepoName, int TotalTemplates, IReadOnlyList<string> Missing, IReadOnlyList<string> Present)
{
    /// <summary>True when none is missing.</summary>
    public bool IsFullyAligned => Missing.Count == 0;
}

/// <summary>What syncing a repo's templates did.</summary>
/// <param name="Changed">True when a file was, or on a dry run would be, written.</param>
/// <param name="Messages">A line per file written.</param>
public sealed record TemplateSync(bool Changed, IReadOnlyList<string> Messages);

/// <summary>The values the templates render from.</summary>
/// <param name="RepoName">The repo's short name.</param>
/// <param name="RepoFullName">Its <c>owner/name</c>.</param>
/// <param name="ContactEmail">Where the Code of Conduct sends reports.</param>
public sealed record OssTemplateContext(string RepoName, string RepoFullName, string ContactEmail);

/// <summary>
/// The community health files every governed repo carries: GitHub issue forms, the PR template, CONTRIBUTING.md and
/// the Contributor Covenant. Ported from <c>rig.repo_governance.oss_templates</c>; the Jinja templates are embedded
/// and use only <c>{{ name }}</c> and <c>{{ name | default("…") }}</c>, which <see cref="Render"/> fills.
/// </summary>
public static partial class OssTemplates
{
    /// <summary>Each template, by file name, and the path it renders to in the repo.</summary>
    public static readonly IReadOnlyList<(string Template, string Target)> Mappings =
    [
        ("bug_report.yml.jinja", ".github/ISSUE_TEMPLATE/bug_report.yml"),
        ("feature_request.yml.jinja", ".github/ISSUE_TEMPLATE/feature_request.yml"),
        ("config.yml.jinja", ".github/ISSUE_TEMPLATE/config.yml"),
        ("PULL_REQUEST_TEMPLATE.md.jinja", ".github/PULL_REQUEST_TEMPLATE.md"),
        ("CONTRIBUTING.md.jinja", "CONTRIBUTING.md"),
        ("CODE_OF_CONDUCT.md.jinja", "CODE_OF_CONDUCT.md"),
    ];

    /// <summary>Which of the templates <paramref name="repoPath"/> has.</summary>
    public static OssTemplatesAudit Inspect(string repoPath)
    {
        var missing = new List<string>();
        var present = new List<string>();
        foreach (var (_, target) in Mappings)
        {
            (Path.Exists(Path.Join(repoPath, target)) ? present : missing).Add(target);
        }

        return new(Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath)), Mappings.Count, missing, present);
    }

    /// <summary>The embedded <paramref name="template"/> filled from <paramref name="context"/>, trimmed and ending in one newline.</summary>
    public static string Render(string template, OssTemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["repo_name"] = context.RepoName,
            ["repo_full_name"] = context.RepoFullName,
            ["contact_email"] = context.ContactEmail,
        };
        var filled = Placeholder().Replace(
            Source(template),
            match => values.TryGetValue(match.Groups["name"].Value, out var value) ? value : match.Groups["fallback"].Value);
        return filled.Trim() + "\n";
    }

    /// <summary>
    /// Writes every template whose rendered text differs from what <paramref name="repoPath"/> holds (ignoring
    /// surrounding whitespace). Nothing is written on a dry run.
    /// </summary>
    public static TemplateSync Sync(string repoPath, OssTemplateContext context, bool dryRun)
    {
        var messages = new List<string>();
        foreach (var (template, target) in Mappings)
        {
            var rendered = Render(template, context);
            var file = Path.Join(repoPath, target);
            if (File.Exists(file) && Licenses.ReadText(file).Trim() == rendered.Trim())
            {
                continue;
            }

            if (dryRun)
            {
                messages.Add($"[Dry Run] Would write {target}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, rendered);
            messages.Add($"Wrote {target}");
        }

        return new(messages.Count > 0, messages);
    }

    private static string Source(string template)
    {
        using var stream = typeof(OssTemplates).Assembly.GetManifestResourceStream($"Rig.Core.Governance.Templates.oss.{template}")
            ?? throw new ArgumentException($"no embedded template {template}", nameof(template));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    [GeneratedRegex("""\{\{\s*(?<name>\w+)\s*(?:\|\s*default\("(?<fallback>[^"]*)"\)\s*)?\}\}""", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
