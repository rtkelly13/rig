using Rig.Core.Vault;

namespace Rig.Core.Governance;

/// <summary>Whether a review tool's config is in a repo.</summary>
/// <param name="Status"><c>CONFIGURED</c>, <c>MISSING</c>, or for Qodana <c>APPLICABLE</c> (a .NET repo without it) or <c>OPTIONAL</c>.</param>
/// <param name="IsAligned">Whether it conforms.</param>
/// <param name="File">The config file found, if any.</param>
public sealed record ConfigStatus(string Status, bool IsAligned, string? File = null);

/// <summary>What aligning one tool's config did.</summary>
/// <param name="Tool">The tool and its file, as reported.</param>
/// <param name="Changed">True when a file was, or on a dry run would be, written.</param>
/// <param name="Message">What happened.</param>
public sealed record ToolChange(string Tool, bool Changed, string Message);

/// <summary>
/// The in-repo config files of the code review tools: CodeRabbit, Codecov, DeepSource and Qodana. Each is written only
/// when absent; an existing file is never overwritten. Ported from <c>rig.repo_governance.review_tool_configs</c>.
/// </summary>
public static class ReviewToolConfigs
{
    private static readonly HashSet<string> DotnetMarkerNames = ["Directory.Build.props", "global.json"];
    private static readonly string[] DotnetMarkerSuffixes = [".sln", ".slnx", ".csproj", ".fsproj"];
    private static readonly HashSet<string> DotnetPruneDirs = ["node_modules", "temp", "dist", "build", "coverage", "vendor", "bin", "obj"];
    private static readonly string[] JsSuffixes = [".ts", ".tsx", ".js", ".jsx"];
    private static readonly string[] SkippedPrefixes = [".", "node_modules", "temp", "vendor", "dist", "bin", "obj"];

    /// <summary>Each tool's status in <paramref name="repoPath"/>, in report order.</summary>
    public static IReadOnlyList<(string Tool, ConfigStatus Status)> Inspect(string repoPath) =>
    [
        ("coderabbit", InspectCodeRabbit(repoPath)),
        ("codecov", InspectCodecov(repoPath)),
        ("deepsource", InspectDeepSource(repoPath)),
        ("qodana", InspectQodana(repoPath)),
    ];

    /// <summary>Whether <c>.coderabbit.yaml</c> (or <c>.yml</c>) is at the root.</summary>
    public static ConfigStatus InspectCodeRabbit(string repoPath) =>
        FirstExisting(repoPath, ".coderabbit.yaml", ".coderabbit.yml") is { } found ? new("CONFIGURED", true, found) : new("MISSING", false);

    /// <summary>Writes the canonical <c>.coderabbit.yaml</c> when there is none.</summary>
    public static (bool Changed, string Message) AlignCodeRabbit(string repoPath, bool dryRun) =>
        InspectCodeRabbit(repoPath).IsAligned ? (false, "already configured") : Create(repoPath, ".coderabbit.yaml", Template("coderabbit.yaml"), dryRun);

    /// <summary>Whether a Codecov config is at the root or in <c>.github/</c>.</summary>
    public static ConfigStatus InspectCodecov(string repoPath) =>
        FirstExisting(repoPath, "codecov.yml", ".codecov.yml", Path.Join(".github", "codecov.yml")) is { } found ? new("CONFIGURED", true, found) : new("MISSING", false);

    /// <summary>Writes the canonical <c>codecov.yml</c> when there is none.</summary>
    public static (bool Changed, string Message) AlignCodecov(string repoPath, bool dryRun) =>
        InspectCodecov(repoPath).IsAligned ? (false, "already configured") : Create(repoPath, "codecov.yml", Template("codecov.yml"), dryRun);

    /// <summary>Whether <c>.deepsource.toml</c> is at the root.</summary>
    public static ConfigStatus InspectDeepSource(string repoPath) =>
        FirstExisting(repoPath, ".deepsource.toml") is { } found ? new("CONFIGURED", true, found) : new("MISSING", false);

    /// <summary>Writes a <c>.deepsource.toml</c> for the repo's languages when there is none.</summary>
    public static (bool Changed, string Message) AlignDeepSource(string repoPath, bool dryRun) =>
        InspectDeepSource(repoPath).IsAligned ? (false, "already configured") : Create(repoPath, ".deepsource.toml", DeepSourceConfig(repoPath), dryRun);

    /// <summary>Qodana's config; only a .NET repo needs it, and its workflow too.</summary>
    public static ConfigStatus InspectQodana(string repoPath)
    {
        var dotnet = IsDotnetRepo(repoPath);
        if (FirstExisting(repoPath, "qodana.yaml", "qodana.yml") is { } found)
        {
            return new("CONFIGURED", File.Exists(QodanaWorkflow(repoPath)) || !dotnet, found);
        }

        return new(dotnet ? "APPLICABLE" : "OPTIONAL", !dotnet);
    }

    /// <summary>Writes <c>qodana.yaml</c> and its workflow, whichever is missing, in a .NET repo.</summary>
    public static (bool Changed, string Message) AlignQodana(string repoPath, bool dryRun)
    {
        if (!IsDotnetRepo(repoPath))
        {
            return (false, "not a .NET repository (skipped)");
        }

        var created = new List<string>();
        if (FirstExisting(repoPath, "qodana.yaml", "qodana.yml") is null)
        {
            if (!dryRun)
            {
                File.WriteAllText(Path.Join(repoPath, "qodana.yaml"), Template("qodana.yaml"));
            }

            created.Add("qodana.yaml");
        }

        var workflow = QodanaWorkflow(repoPath);
        if (!File.Exists(workflow))
        {
            if (!dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(workflow)!);
                File.WriteAllText(workflow, Template("qodana-dotnet.yml"));
            }

            created.Add(".github/workflows/qodana.yml");
        }

        return created.Count == 0 ? (false, "already configured") : (true, $"{(dryRun ? "would create" : "created")} {string.Join(", ", created)}");
    }

    /// <summary>
    /// Aligns every tool's config in <paramref name="repoPath"/>. A tool whose app disallows the repo on
    /// <paramref name="org"/> is skipped, except CodeRabbit, which gets a suppressor so an all-repos install stays quiet;
    /// an existing CodeRabbit config that does not suppress is reported as a conflict, never overwritten.
    /// </summary>
    public static IReadOnlyList<ToolChange> Align(string repoPath, string org, RepoPolicies policies, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var repo = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
        var results = new List<ToolChange>();
        const string CodeRabbit = "CodeRabbit (.coderabbit.yaml)";
        if (policies.IsAppDisallowed(repo, org, "coderabbitai"))
        {
            var existing = FirstExisting(repoPath, ".coderabbit.yaml", ".coderabbit.yml");
            if (existing is null)
            {
                if (!dryRun)
                {
                    File.WriteAllText(Path.Join(repoPath, ".coderabbit.yaml"), Template("coderabbit-suppressor.yaml"));
                }

                results.Add(new(CodeRabbit, true, "disallowed by policy: configured suppressor (auto_review: false)"));
            }
            else if (AutoReviewDisabled(existing))
            {
                results.Add(new(CodeRabbit, false, "disallowed by policy: suppressor already present"));
            }
            else
            {
                results.Add(new(CodeRabbit, false, $"CONFLICT: disallowed by policy but {Path.GetFileName(existing)} does not set reviews.auto_review.enabled: false; left unchanged"));
            }
        }
        else
        {
            var (changed, message) = AlignCodeRabbit(repoPath, dryRun);
            results.Add(new(CodeRabbit, changed, message));
        }

        Add("Codecov (codecov.yml)", "codecov", AlignCodecov);
        Add("DeepSource (.deepsource.toml)", "deepsource-io", AlignDeepSource);
        Add("Qodana (qodana.yaml & workflow)", "qodana", AlignQodana);
        return results;

        void Add(string tool, string slug, Func<string, bool, (bool Changed, string Message)> align)
        {
            if (policies.IsAppDisallowed(repo, org, slug))
            {
                results.Add(new(tool, false, "disallowed by policy: skipped"));
                return;
            }

            var (changed, message) = align(repoPath, dryRun);
            results.Add(new(tool, changed, message));
        }
    }

    /// <summary>Whether a .NET solution, project or SDK marker is anywhere below the root, skipping hidden and build trees.</summary>
    public static bool IsDotnetRepo(string repoPath)
    {
        var pending = new Stack<string>();
        pending.Push(repoPath);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (Directory.EnumerateFiles(directory, "*", options).Select(Path.GetFileName).Any(name => DotnetMarkerNames.Contains(name!) || DotnetMarkerSuffixes.Any(suffix => name!.EndsWith(suffix, StringComparison.Ordinal))))
            {
                return true;
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !DotnetPruneDirs.Contains(name) && !new FileInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child);
                }
            }
        }

        return false;
    }

    /// <summary>A <c>.deepsource.toml</c> enabling the analyzers for the languages at the root and one level down.</summary>
    public static string DeepSourceConfig(string repoPath)
    {
        var (python, js) = (false, false);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var entry in Directory.EnumerateFileSystemEntries(repoPath, "*", options))
        {
            var name = Path.GetFileName(entry);
            if (name is "pyproject.toml" or "requirements.txt" or "Pipfile" or "setup.py" || name.EndsWith(".py", StringComparison.Ordinal))
            {
                python = true;
            }
            else if (name is "package.json" or "tsconfig.json" || JsSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
            {
                js = true;
            }
            else if (Directory.Exists(entry) && !SkippedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                foreach (var sub in Directory.EnumerateFileSystemEntries(entry, "*", options).Select(Path.GetFileName))
                {
                    if (sub!.EndsWith(".py", StringComparison.Ordinal) || sub is "pyproject.toml" or "requirements.txt")
                    {
                        python = true;
                    }
                    else if (JsSuffixes.Any(suffix => sub.EndsWith(suffix, StringComparison.Ordinal)) || sub is "package.json" or "tsconfig.json")
                    {
                        js = true;
                    }
                }
            }
        }

        var analyzers = new List<string>();
        if (python)
        {
            analyzers.Add("[[analyzers]]\nname = \"python\"\nenabled = true\n\n[analyzers.meta]\nruntime_version = \"3.x.x\"");
        }

        if (js)
        {
            analyzers.Add("[[analyzers]]\nname = \"javascript\"\nenabled = true\n\n[analyzers.meta]\nenvironment = [\"nodejs\"]");
        }

        if (IsDotnetRepo(repoPath))
        {
            analyzers.Add("[[analyzers]]\nname = \"csharp\"\nenabled = true");
        }

        analyzers.Add("[[analyzers]]\nname = \"secrets\"\nenabled = true");
        return Template("deepsource.toml").Replace("{analyzers}", string.Join("\n\n", analyzers), StringComparison.Ordinal);
    }

    // True only when the YAML sets reviews.auto_review.enabled to exactly false.
    private static bool AutoReviewDisabled(string file)
    {
        PyValue? parsed;
        try
        {
            parsed = PyYaml.Load(Licenses.ReadText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyYamlException)
        {
            return false;
        }

        return Lookup(Lookup(Lookup(parsed, "reviews"), "auto_review"), "enabled") is PyScalar { Rendered: "False" };

        static PyValue? Lookup(PyValue? value, string key) =>
            value is PyDict dict ? dict.Entries.FirstOrDefault(entry => entry.Key is PyStr { Text: var text } && text == key).Value : null;
    }

    private static (bool Changed, string Message) Create(string repoPath, string name, string content, bool dryRun)
    {
        if (dryRun)
        {
            return (true, $"dry-run: would create {name}");
        }

        File.WriteAllText(Path.Join(repoPath, name), content);
        return (true, $"created {name}");
    }

    private static string? FirstExisting(string repoPath, params string[] names) =>
        names.Select(name => Path.Join(repoPath, name)).FirstOrDefault(Path.Exists);

    private static string QodanaWorkflow(string repoPath) => Path.Join(repoPath, ".github", "workflows", "qodana.yml");

    private static string Template(string name)
    {
        using var stream = typeof(ReviewToolConfigs).Assembly.GetManifestResourceStream($"Rig.Core.Governance.Templates.review.{name}")
            ?? throw new ArgumentException($"no embedded template {name}", nameof(name));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
