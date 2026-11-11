using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>The repo facts the policy templates render from.</summary>
/// <param name="RepoName">Its <c>owner/name</c> when gh knows it, else the clone's folder name.</param>
/// <param name="IsPublic">True when gh says the repo is not private; false when gh cannot say.</param>
/// <param name="License">The SPDX id gh reports, else <c>MIT</c>.</param>
/// <param name="AuthorEmail">Where SECURITY.md sends reports: the policy's contact address.</param>
/// <param name="SupportedRuntimes">The runtimes the test suites cover, guessed from the files at the root.</param>
public sealed record RepoMetadata(string RepoName, bool IsPublic, string License, string AuthorEmail, string SupportedRuntimes);

/// <summary>
/// The governance policy files <c>apply-policy</c> and <c>sync-policies</c> write: AI_POLICY.md, SECURITY.md and the
/// README's AI disclosure. Ported from <c>rig.repo_governance.policy_template_engine</c>; the Jinja templates are
/// embedded and use only <c>{{ name }}</c>, which <see cref="OssTemplates.Fill"/> fills.
/// </summary>
/// <param name="proc">Runs gh.</param>
/// <param name="git">Reads the origin, so gh describes it rather than an <c>upstream</c> remote.</param>
public sealed class PolicyFiles(IProc proc, Git git)
{
    /// <summary>The badge the README gains, linking the AI policy.</summary>
    public const string Badge =
        "[![AI Assisted](https://img.shields.io/badge/development-AI%20assisted-792ee5.svg?logo=openai&logoColor=white)](AI_POLICY.md)";

    /// <summary>The heading of the README section; its presence means the section is already there.</summary>
    public const string SectionHeading = "## 🤖 AI Utilization & Transparency";

    private static readonly TimeSpan ViewTimeout = TimeSpan.FromSeconds(30);

    /// <summary>What gh and the files at <paramref name="repoPath"/>'s root say, with the contact address from <paramref name="policies"/>.</summary>
    public async Task<RepoMetadata> DetectAsync(string repoPath, RepoPolicies policies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var repoName = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
        var isPublic = false;
        var license = "MIT";

        var origin = (await git.RunAsync(repoPath, ["remote", "get-url", "origin"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        var view = await proc.RunAsync(
            new ProcRequest(["gh", "repo", "view", .. origin.Length > 0 ? [origin] : Array.Empty<string>(), "--json", "nameWithOwner,isPrivate,visibility,licenseInfo"])
            {
                WorkingDirectory = repoPath,
                Timeout = ViewTimeout,
            },
            cancellationToken).ConfigureAwait(false);
        if (view.Ok && Parse(view.Out) is { } data)
        {
            repoName = data["nameWithOwner"] is JsonValue name && name.TryGetValue<string>(out var full) && full.Length > 0 ? full : repoName;
            isPublic = !(data["isPrivate"] is JsonValue priv && priv.TryGetValue<bool>(out var isPrivate) ? isPrivate : true);
            if (data["licenseInfo"]?["spdxId"] is JsonValue spdx && spdx.TryGetValue<string>(out var id) && id.Length > 0)
            {
                license = id;
            }
        }

        return new(repoName, isPublic, license, policies.ContactEmail(repoName), Runtimes(repoPath));
    }

    /// <summary>The runtimes guessed from <paramref name="repoPath"/>'s root: Python, then Node, else .NET.</summary>
    public static string Runtimes(string repoPath) =>
        File.Exists(Path.Join(repoPath, "pyproject.toml")) || File.Exists(Path.Join(repoPath, "setup.py")) ? "Python 3.11+"
        : File.Exists(Path.Join(repoPath, "package.json")) ? "Node.js 20+ / TypeScript"
        : ".NET 8 & .NET 9";

    /// <summary>
    /// The embedded <paramref name="template"/> filled from <paramref name="meta"/>, less its final newline (as Jinja
    /// renders it).
    /// </summary>
    public static string Render(string template, RepoMetadata meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["repo_name"] = meta.RepoName,
            ["license"] = meta.License,
            ["author_email"] = meta.AuthorEmail,
            ["supported_runtimes"] = meta.SupportedRuntimes,
        };
        var filled = OssTemplates.Fill("policy", template, values);
        return filled.EndsWith('\n') ? filled[..^1] : filled;
    }

    /// <summary>Writes AI_POLICY.md, unless <paramref name="dryRun"/>.</summary>
    public static void ApplyAiPolicy(string repoPath, RepoMetadata meta, bool dryRun) => Write(repoPath, "AI_POLICY.md", meta, dryRun);

    /// <summary>Writes SECURITY.md, unless <paramref name="dryRun"/>.</summary>
    public static void ApplySecurityPolicy(string repoPath, RepoMetadata meta, bool dryRun) => Write(repoPath, "SECURITY.md", meta, dryRun);

    /// <summary>
    /// Gives README.md the AI badge (after the last badge in its first 15 lines, else at the top) and the AI section
    /// (before every License heading, else at the end), each only when absent. Nothing is written on a dry run; false
    /// when there is no README.
    /// </summary>
    public static bool InjectReadmeAiDisclosure(string repoPath, RepoMetadata meta, bool dryRun)
    {
        var readme = Path.Join(repoPath, "README.md");
        if (!File.Exists(readme))
        {
            return false;
        }

        var text = Licenses.ReadText(readme);
        if (!text.Contains("AI%20assisted", StringComparison.Ordinal) && !text.Contains("[![AI Assisted]", StringComparison.Ordinal))
        {
            var lines = SplitLines(text);
            var badgeAt = -1;
            for (var i = 0; i < Math.Min(15, lines.Count); i++)
            {
                if (lines[i].StartsWith("[![", StringComparison.Ordinal) || lines[i].StartsWith("[! [", StringComparison.Ordinal))
                {
                    badgeAt = i;
                }
            }

            if (badgeAt != -1)
            {
                // Rejoining the lines drops a final newline, as Python's splitlines and join did.
                lines.Insert(badgeAt + 1, Badge);
                text = string.Join('\n', lines);
            }
            else
            {
                text = Badge + "\n\n" + text;
            }
        }

        if (!text.Contains(SectionHeading, StringComparison.Ordinal))
        {
            var section = Render("README_AI_SECTION.md.jinja", meta);
            text = text.Contains("## 📄 License", StringComparison.Ordinal)
                ? text.Replace("## 📄 License", $"{section}\n\n---\n\n## 📄 License", StringComparison.Ordinal)
                : text.Contains("## License", StringComparison.Ordinal)
                    ? text.Replace("## License", $"{section}\n\n---\n\n## License", StringComparison.Ordinal)
                    : text.TrimEnd() + $"\n\n---\n\n{section}\n";
        }

        if (!dryRun)
        {
            File.WriteAllText(readme, text);
        }

        return true;
    }

    private static void Write(string repoPath, string target, RepoMetadata meta, bool dryRun)
    {
        var content = Render($"{target}.jinja", meta);
        if (!dryRun)
        {
            File.WriteAllText(Path.Join(repoPath, target), content.Trim() + "\n");
        }
    }

    // Python's str.splitlines over text whose line endings are already "\n": no empty entry after a final newline.
    private static List<string> SplitLines(string text)
    {
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
