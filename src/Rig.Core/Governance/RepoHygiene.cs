using System.Text;
using System.Text.RegularExpressions;
using Rig.Core.Vault;

namespace Rig.Core.Governance;

/// <summary>The outcome of one in-repo hygiene inspection or alignment.</summary>
/// <param name="Status">The verdict as printed: <c>CONFIGURED</c>, <c>MISSING</c>, <c>aligned</c>, <c>skipped</c> and so on.</param>
/// <param name="IsAligned">True when nothing needs changing.</param>
/// <param name="File">The file looked at or written, when there is one.</param>
/// <param name="Reason">Why, when the status alone does not say.</param>
/// <param name="UnpinnedCount">The <c>uses:</c> refs not pinned to a commit, for the pinned-SHA check.</param>
public sealed record HygieneResult(string Status, bool IsAligned = false, string? File = null, string? Reason = null, int UnpinnedCount = 0);

/// <summary>
/// The files inside a checkout the conventions require: AGENTS.md's conventions block, <c>.npmrc</c>, the pre-push hook,
/// <c>TODO.md</c>, <c>.ls-lint.yml</c>, pinned action SHAs and the Dependabot/Renovate split. Each check reads; each
/// align writes only when not a dry run. Ported from <c>rig.repo_governance.repo_hygiene</c>.
/// </summary>
public static partial class RepoHygiene
{
    /// <summary>The heading that marks the governance-owned conventions block.</summary>
    public const string AgentsConventionsHeading = "## 🛑 Repository Conventions & Workflow Policy";

    /// <summary>The conventions block every AGENTS.md declares.</summary>
    public const string ExpectedAgentsConventions = """
        ## 🛑 Repository Conventions & Workflow Policy

        1. **Squash Merge Only**: All pull requests must be merged into `main` using **Squash and Merge** exclusively.
        2. **Delete Branch on Merge**: Feature branches must be automatically deleted immediately upon merge into `main`.
        3. **Linear History**: Maintain a strictly linear history. Rebase feature branches onto `main` before merging.
        4. **Direct Push Protection**: Direct pushes to `main` are blocked; PR mechanism required (force push allowed).
        5. **Local Temp & Worktree Directory**: All temporary files, databases, and worktrees go in `/temp/` (gitignored).
        6. **Gitignored Local TODO File**: A root `TODO.md` file MUST exist for local task tracking and be gitignored.
        7. **Auto-Merge Enabled**: PRs may enable auto-merge (squash) so they merge automatically once required checks pass.
        8. **Pinned Action SHAs**: All GitHub Actions workflows MUST use 40-character commit SHAs instead of mutable tags.
        9. **Script Language Policy**: Automation scripts in `scripts/` MUST use the repository's approved
           language stack (e.g., C# `.cs` for .NET, TypeScript/JavaScript for Node, Python `.py` for Python).
           Alien scripting runtimes (e.g., `.py` in C# repos, `.sh`/`.ps1` everywhere) are prohibited.

        """;

    /// <summary>The pre-push hook: blocks direct pushes to main, then hands the push to git-lfs.</summary>
    public const string PrePushHookContent = """
        #!/usr/bin/env bash
        # Enforced by align-repo-conventions
        # Prevents accidental direct pushes to main branch without explicit bypass,
        # then hands the push to git-lfs so LFS content is uploaded with the commits.

        # Git passes the refs being pushed on stdin; keep them for git-lfs.
        refs=$(cat)

        while read local_ref local_oid remote_ref remote_oid
        do
        	if [ "$remote_ref" = "refs/heads/main" ]; then
        		if [ "$ALLOW_MAIN_PUSH" != "1" ]; then
        			echo "🛑 DIRECT PUSH TO MAIN BLOCKED by repository governance policy!"
        			echo "Push your changes to a feature branch and merge via PR (gh pr merge --squash)."
        			echo "Override for emergency push: ALLOW_MAIN_PUSH=1 git push origin main"
        			exit 1
        		fi
        	fi
        done <<< "$refs"

        if command -v git-lfs >/dev/null 2>&1; then
        	printf '%s\n' "$refs" | git lfs pre-push "$@" || exit $?
        fi

        exit 0

        """;

    /// <summary>The <c>.npmrc</c> line that points the <c>@rtkelly13</c> scope at GitHub Packages.</summary>
    public const string NpmrcRegistry = "@rtkelly13:registry=https://npm.pkg.github.com";

    /// <summary>Renovate's config files, in the order Renovate looks for them.</summary>
    public static readonly IReadOnlyList<string> RenovateConfigFiles =
    [
        "renovate.json", "renovate.json5", ".renovaterc", ".renovaterc.json", ".renovaterc.json5",
        ".github/renovate.json", ".github/renovate.json5", ".github/.renovaterc", ".github/.renovaterc.json", ".github/.renovaterc.json5",
    ];

    /// <summary>Dependabot's config files.</summary>
    public static readonly IReadOnlyList<string> DependabotConfigFiles = [".github/dependabot.yml", ".github/dependabot.yaml"];

    // Extensions every scripts/ folder may hold whatever its language: docs, data and config.
    private static readonly string[] CommonScriptSupportExtensions = [".dir", ".md", ".json", ".yml", ".yaml", ".toml", ".xml", ".sql", ".txt"];

    private const string MainBlockMarker = "DIRECT PUSH TO MAIN BLOCKED";

    // pre-push is the hook git-lfs uploads LFS content from, so a hook without it pushes pointers and no content.
    private const string LfsHandoffMarker = "git lfs pre-push";

    private const string ConventionsTitle = "Repository Conventions & Workflow Policy";

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Whether AGENTS.md exists and declares the conventions: <c>MISSING</c>, <c>PARTIAL</c> or <c>CONFIGURED</c>.</summary>
    public static HygieneResult InspectAgentsMd(string repoPath)
    {
        var file = Path.Join(repoPath, "AGENTS.md");
        if (!Path.Exists(file))
        {
            return new("MISSING");
        }

        var content = DocsTree.ReadText(file);
        return content.Contains(ConventionsTitle, StringComparison.Ordinal) && content.Contains("TODO.md", StringComparison.Ordinal)
            ? new("CONFIGURED", true)
            : new("PARTIAL");
    }

    /// <summary>
    /// Creates AGENTS.md when missing and appends the conventions block when the section is absent. A section that is
    /// present but differs is reported (<c>divergent</c>), never appended a second time.
    /// </summary>
    public static HygieneResult AlignAgentsMd(string repoPath, bool dryRun)
    {
        var file = Path.Join(repoPath, "AGENTS.md");
        if (!Path.Exists(file))
        {
            Write(file, $"# {FolderName(repoPath).ToUpperInvariant()}\n\n{ExpectedAgentsConventions}", dryRun);
            return new("created", File: file);
        }

        var content = DocsTree.ReadText(file);
        if (!content.Contains(AgentsConventionsHeading, StringComparison.Ordinal) && !content.Contains(ConventionsTitle, StringComparison.Ordinal))
        {
            Write(file, $"{content}\n\n{ExpectedAgentsConventions}", dryRun);
            return new("appended", File: file);
        }

        if (!content.Contains(ExpectedAgentsConventions.Trim(), StringComparison.Ordinal))
        {
            return new(
                "divergent",
                File: file,
                Reason: "The conventions section exists but does not match the expected block. Left as-is — run `repo-governance agents-audit` to review it.");
        }

        return new("already_configured", File: file);
    }

    /// <summary>Whether a Node repo's <c>.npmrc</c> points the scope at GitHub Packages.</summary>
    public static HygieneResult InspectNpmrc(string repoPath)
    {
        if (!Path.Exists(Path.Join(repoPath, "package.json")))
        {
            return new("N/A (No package.json)", true);
        }

        var npmrc = Path.Join(repoPath, ".npmrc");
        return Path.Exists(npmrc) && DocsTree.ReadText(npmrc).Contains(NpmrcRegistry, StringComparison.Ordinal)
            ? new("CONFIGURED", true)
            : new("MISSING / DRIFTED");
    }

    /// <summary>Prepends the registry line to a Node repo's <c>.npmrc</c> when it lacks one.</summary>
    public static HygieneResult AlignNpmrc(string repoPath, bool dryRun)
    {
        if (!Path.Exists(Path.Join(repoPath, "package.json")))
        {
            return new("skipped", Reason: "No package.json found");
        }

        var npmrc = Path.Join(repoPath, ".npmrc");
        var content = Path.Exists(npmrc) ? DocsTree.ReadText(npmrc) : string.Empty;
        if (content.Contains(NpmrcRegistry, StringComparison.Ordinal))
        {
            return new("already_configured");
        }

        Write(npmrc, NpmrcRegistry + "\n" + content, dryRun);
        return new("aligned", File: npmrc);
    }

    /// <summary>
    /// Counts the <c>uses:</c> refs in <c>.github/workflows</c> not pinned to a 40-character commit SHA. Read-only:
    /// pinning is pinact's and Dependabot's job; see docs/repo-governance/action-pinning.md.
    /// </summary>
    public static HygieneResult InspectPinnedShas(string repoPath)
    {
        var workflows = WorkflowFiles(repoPath);
        if (workflows.Count == 0)
        {
            return new("N/A (No Workflows)", true);
        }

        var unpinned = 0;
        foreach (var file in workflows)
        {
            foreach (var line in DocsTree.ReadText(file).Split('\n'))
            {
                if (UsesPattern().Match(line) is { Success: true } match && !HexSha().IsMatch(match.Groups[3].Value))
                {
                    unpinned++;
                }
            }
        }

        return unpinned == 0 ? new("PINNED", true) : new($"UNPINNED ({unpinned} actions)", UnpinnedCount: unpinned);
    }

    /// <summary>Whether <c>.git/hooks/pre-push</c> blocks main pushes and still uploads LFS content.</summary>
    public static HygieneResult InspectGitHooks(string repoPath)
    {
        var hook = Path.Join(repoPath, ".git", "hooks", "pre-push");
        if (Path.Exists(hook))
        {
            var content = DocsTree.ReadText(hook);
            if (HookIsCurrent(content))
            {
                return new("ENFORCED", true);
            }

            if (Runs(content, MainBlockMarker))
            {
                return new("OUTDATED", Reason: "pre-push hook skips git-lfs uploads");
            }
        }

        return new("MISSING");
    }

    /// <summary>Installs the pre-push hook, executable, unless the current one is already in place.</summary>
    public static HygieneResult AlignGitHooks(string repoPath, bool dryRun)
    {
        var hooks = Path.Join(repoPath, ".git", "hooks");
        if (!Directory.Exists(hooks))
        {
            return new("skipped", Reason: "No .git/hooks directory found");
        }

        var hook = Path.Join(hooks, "pre-push");
        if (Path.Exists(hook) && HookIsCurrent(DocsTree.ReadText(hook)))
        {
            return new("already_configured");
        }

        if (!dryRun)
        {
            System.IO.File.WriteAllText(hook, PrePushHookContent, Utf8);
            if (!OperatingSystem.IsWindows())
            {
                System.IO.File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
            }
        }

        return new("aligned", File: hook);
    }

    /// <summary>Whether a root <c>TODO.md</c> exists, spelled exactly so.</summary>
    public static HygieneResult InspectTodoMd(string repoPath) =>
        EntryNames(repoPath).Contains("TODO.md") ? new("PRESENT", true) : new("MISSING");

    /// <summary>
    /// Ensures a root <c>TODO.md</c>: renames a lowercase <c>todo.md</c> (through a temporary name, so a
    /// case-insensitive disk keeps its content), else creates one.
    /// </summary>
    public static HygieneResult AlignTodoMd(string repoPath, bool dryRun)
    {
        var todo = Path.Join(repoPath, "TODO.md");
        var names = EntryNames(repoPath);
        if (names.Contains("todo.md") && !names.Contains("TODO.md"))
        {
            if (!dryRun)
            {
                var lower = Path.Join(repoPath, "todo.md");
                var staging = Path.Join(repoPath, $".todo.md.{Guid.NewGuid():N}");
                System.IO.File.Move(lower, staging);
                System.IO.File.Move(staging, todo);
            }

            return new("renamed_to_uppercase", File: todo);
        }

        if (Path.Exists(todo))
        {
            return new("already_exists");
        }

        Write(todo, $"# TODO: {FolderName(repoPath).ToUpperInvariant()}\n\n- [ ] Local tasks and notes (gitignored)\n", dryRun);
        return new("aligned", File: todo);
    }

    /// <summary>
    /// Whether <c>.ls-lint.yml</c> bans the scripts the policy bans, lists its exceptions and carries the
    /// <c>scripts/</c> language rules. <paramref name="key"/> is the repo's policy key.
    /// </summary>
    public static HygieneResult InspectLsLint(string repoPath, RepoPolicies? policies, string key)
    {
        var file = Path.Join(repoPath, ".ls-lint.yml");
        if (!Path.Exists(file))
        {
            return new("MISSING");
        }

        var content = DocsTree.ReadText(file);
        IEnumerable<string> expectedBanned = [".sh", ".ps1"];
        var ban = policies?.ScriptBan(key);
        if (ban is not null)
        {
            expectedBanned = ban.Enabled ? expectedBanned.Where(ban.BannedExtensions.Select(Dotted).Contains) : [];
        }

        if (expectedBanned.Any(ext => !content.Contains($"{ext}: exists:0", StringComparison.Ordinal)))
        {
            return new("INCOMPLETE_RULES", File: file);
        }

        if (policies is null)
        {
            return new("CONFIGURED", true, file);
        }

        if (ban is { Enabled: true } && ban.AllowedExceptions.FirstOrDefault(exception => !content.Contains(exception, StringComparison.Ordinal)) is { } missing)
        {
            return new("MISSING_EXCEPTIONS", File: file, Reason: $"Missing allowed exception: {missing}");
        }

        var rules = policies.ScriptRules(key, repoPath);
        if (rules.AllowedExtensions.Count > 0 || rules.BannedExtensions.Count > 0)
        {
            if (!content.Contains("scripts:", StringComparison.Ordinal))
            {
                return new("MISSING_SCRIPTS_RULES", File: file, Reason: "Missing scripts folder language rules");
            }

            if (rules.BannedExtensions.Select(Dotted).FirstOrDefault(ext => !content.Contains($"{ext}: exists:0", StringComparison.Ordinal)) is { } banned)
            {
                return new("MISSING_BANNED_EXTENSION", File: file, Reason: $"Missing banned script extension: {banned}");
            }

            if (rules.AllowedExtensions.Select(Dotted).FirstOrDefault(ext => !content.Contains($"{ext}: regex:.*", StringComparison.Ordinal)) is { } allowed)
            {
                return new("MISSING_ALLOWED_EXTENSION", File: file, Reason: $"Missing allowed script extension: {allowed}");
            }
        }

        return new("CONFIGURED", true, file);
    }

    /// <summary>
    /// The <c>.ls-lint.yml</c> that bans <paramref name="bannedExtensions"/> everywhere (by default shell and
    /// PowerShell scripts), with the given exceptions and <c>scripts/</c> rules.
    /// </summary>
    public static string RenderLsLintConfig(
        IReadOnlyList<string> allowedExceptions,
        IReadOnlyList<string> allowedScriptExtensions,
        IReadOnlyList<string> bannedScriptExtensions,
        IReadOnlyList<string>? bannedExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(allowedExceptions);
        ArgumentNullException.ThrowIfNull(allowedScriptExtensions);
        ArgumentNullException.ThrowIfNull(bannedScriptExtensions);
        var banned = (bannedExtensions ?? ScriptBanRules.Default.BannedExtensions).Select(Dotted).ToList();
        List<string> lines =
        [
            "# .ls-lint.yml",
            banned.SequenceEqual(ScriptBanRules.Default.BannedExtensions)
                ? "# Estate-wide ban on shell (.sh, .bash, .zsh) and PowerShell (.ps1, .psm1) scripts."
                : banned.Count > 0 ? $"# Ban on {string.Join(", ", banned)} scripts, as this repo's script_ban policy sets." : "# This repo's script_ban policy bans no scripts.",
            "# Exceptions must be explicitly listed in the `ignore:` section below.",
            string.Empty,
            // An empty `ls:` is null to ls-lint; `{}` keeps the file valid when the ban and scripts/ rules are both empty.
            banned.Count > 0 || allowedScriptExtensions.Count > 0 || bannedScriptExtensions.Count > 0 ? "ls:" : "ls: {}",
            .. banned.Select(ext => $"  {ext}: exists:0"),
        ];
        if (banned.Count > 0)
        {
            lines.Add("  \"**\":");
            lines.AddRange(banned.Select(ext => $"    {ext}: exists:0"));
        }

        if (allowedScriptExtensions.Count > 0 || bannedScriptExtensions.Count > 0)
        {
            foreach (var scope in new[] { "scripts", "\"scripts/**\"" })
            {
                lines.Add($"  {scope}:");
                lines.AddRange(CommonScriptSupportExtensions.Select(ext => $"    {ext}: regex:.*"));
                lines.AddRange(allowedScriptExtensions.Select(Dotted).Where(ext => !CommonScriptSupportExtensions.Contains(ext)).Select(ext => $"    {ext}: regex:.*"));
                lines.AddRange(bannedScriptExtensions.Select(ext => $"    {Dotted(ext)}: exists:0"));
                lines.Add("    .*: exists:0");
            }
        }

        lines.AddRange(
        [
            string.Empty,
            "ignore:",
            "  - .git",
            "  - .venv",
            "  - \"**/.venv\"",
            "  - node_modules",
            "  - \"**/node_modules\"",
            "  - temp",
            "  - .temp",
            "  - \"**/temp\"",
            "  - .worktrees",
            "  - bin",
            "  - obj",
            "  - dist",
            "  - build",
        ]);
        if (allowedExceptions.Count > 0)
        {
            lines.Add("  # Explicit repository exceptions:");
            lines.AddRange(allowedExceptions.Select(exception => $"  - {exception}"));
        }

        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    /// <summary>Writes the policy's <c>.ls-lint.yml</c> unless the current one already passes <see cref="InspectLsLint"/>.</summary>
    public static HygieneResult AlignLsLint(string repoPath, RepoPolicies? policies, string key, bool dryRun)
    {
        var current = InspectLsLint(repoPath, policies, key);
        if (current.IsAligned)
        {
            return new("already_configured", File: current.File);
        }

        IReadOnlyList<string> exceptions = [];
        IReadOnlyList<string>? banned = null;
        RepoScriptRules rules = new([], []);
        if (policies is not null)
        {
            var ban = policies.ScriptBan(key);
            (exceptions, banned) = ban.Enabled ? (ban.AllowedExceptions, ban.BannedExtensions) : ([], []);
            rules = policies.ScriptRules(key, repoPath);
        }

        var file = Path.Join(repoPath, ".ls-lint.yml");
        Write(file, RenderLsLintConfig(exceptions, rules.AllowedExtensions, rules.BannedExtensions, banned), dryRun);
        return new("aligned", File: file);
    }

    /// <summary>
    /// Flags a repo with both a Renovate config and Dependabot version updates: <c>NO_RENOVATE</c>,
    /// <c>CONFIGURED</c> or <c>CONFLICT</c>. Security-only Dependabot alerts alongside Renovate are fine.
    /// </summary>
    public static HygieneResult InspectDependabotRenovate(string repoPath)
    {
        if (FirstFile(repoPath, RenovateConfigFiles) is not { } renovate)
        {
            return new("NO_RENOVATE", true);
        }

        if (!HasDependabotVersionUpdates(repoPath))
        {
            return new("CONFIGURED", true, renovate);
        }

        var dependabot = FirstFile(repoPath, DependabotConfigFiles) is { } found ? Path.GetFileName(found) : "dependabot.yml";
        return new(
            "CONFLICT",
            File: renovate,
            Reason: $"Conflicting dependency automation: found both Renovate config ({Path.GetFileName(renovate)}) and active version updates in .github/{dependabot}. "
                + "Remove version updates from dependabot.yml or set open-pull-requests-limit: 0 (security-only alerts are fine).");
    }

    /// <summary>
    /// Whether Dependabot opens version-update PRs: an <c>updates</c> entry without <c>open-pull-requests-limit: 0</c>.
    /// A config that does not read as Dependabot's shape counts when it mentions <c>updates:</c>.
    /// </summary>
    public static bool HasDependabotVersionUpdates(string repoPath)
    {
        if (FirstFile(repoPath, DependabotConfigFiles) is not { } file)
        {
            return false;
        }

        string content;
        try
        {
            content = DocsTree.ReadText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return UpdateLimits(content) is { } limits
            ? limits.Any(limit => limit is null or > 0)
            : content.Contains("updates:", StringComparison.Ordinal);
    }

    // Each `updates` entry's open-pull-requests-limit (null when unset); null when the file is not Dependabot's shape.
    private static List<long?>? UpdateLimits(string content)
    {
        PyValue? loaded;
        try
        {
            loaded = PyYaml.Load(content);
        }
        catch (PyYamlException)
        {
            return null;
        }

        if (loaded is not PyDict config)
        {
            return null;
        }

        var updates = config.Entries.LastOrDefault(e => e.Key is PyStr { Text: "updates" }).Value;
        if (updates is null or PyNone)
        {
            return updates is null ? [] : null;
        }

        if (updates is not PyList entries)
        {
            return null;
        }

        var limits = new List<long?>();
        foreach (var entry in entries.Items)
        {
            if (entry is not PyDict update)
            {
                return null;
            }

            switch (update.Entries.LastOrDefault(e => e.Key is PyStr { Text: "open-pull-requests-limit" }).Value)
            {
                case null or PyNone:
                    limits.Add(null);
                    break;
                case PyScalar { Rendered: var rendered } when long.TryParse(rendered, System.Globalization.CultureInfo.InvariantCulture, out var limit):
                    limits.Add(limit);
                    break;
                default:
                    return null;
            }
        }

        return limits;
    }

    private static bool HookIsCurrent(string content) => Runs(content, MainBlockMarker) && Runs(content, LfsHandoffMarker);

    // Whether marker is on a line that runs: a hook that only mentions it in a comment guards nothing.
    private static bool Runs(string content, string marker) =>
        content.Split('\n').Select(line => line.TrimStart()).Any(line => !line.StartsWith('#') && line.Contains(marker, StringComparison.Ordinal));

    private static string Dotted(string extension) => extension.StartsWith('.') ? extension : "." + extension;

    private static string FolderName(string repoPath) => Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));

    // The names in a folder as the disk spells them, so a case-insensitive disk cannot pass todo.md off as TODO.md.
    private static HashSet<string> EntryNames(string repoPath) =>
        Directory.Exists(repoPath) ? [.. Directory.EnumerateFileSystemEntries(repoPath).Select(Path.GetFileName).OfType<string>()] : [];

    private static List<string> WorkflowFiles(string repoPath)
    {
        var workflows = Path.Join(repoPath, ".github", "workflows");
        return Directory.Exists(workflows)
            ? [.. Directory.EnumerateFiles(workflows, "*.yml").Where(f => f.EndsWith(".yml", StringComparison.Ordinal)).Concat(Directory.EnumerateFiles(workflows, "*.yaml"))]
            : [];
    }

    private static string? FirstFile(string repoPath, IEnumerable<string> relatives) =>
        relatives.Select(relative => Path.Join(repoPath, relative)).FirstOrDefault(System.IO.File.Exists);

    private static void Write(string file, string content, bool dryRun)
    {
        if (!dryRun)
        {
            System.IO.File.WriteAllText(file, content, Utf8);
        }
    }

    [GeneratedRegex(@"(uses:\s+[""']?)([A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+)@([A-Za-z0-9_.-]+)(.*)", RegexOptions.CultureInvariant)]
    private static partial Regex UsesPattern();

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexSha();
}
