using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>The vault lint's finding codes, ported from <c>rig.obsidian.lint_rules</c>.</summary>
public static class LintCodes
{
    /// <summary>The frontmatter does not parse.</summary>
    public const string InvalidFrontmatter = "TVS000";

    /// <summary>The note has no <c>type</c>.</summary>
    public const string MissingType = "TVS001";

    /// <summary>A field the type requires is missing.</summary>
    public const string MissingRequiredField = "TVS002";

    /// <summary>A field the type requires is empty.</summary>
    public const string EmptyRequiredField = "TVS003";

    /// <summary>A list the type requires is empty.</summary>
    public const string EmptyRequiredList = "TVS004";

    /// <summary>The <c>type</c> is not one the vault knows.</summary>
    public const string UnknownType = "TVS005";

    /// <summary>The filename is URL-shaped.</summary>
    public const string FilenameUrlish = "TVS101";

    /// <summary>The filename has non-ASCII characters.</summary>
    public const string FilenameNonAscii = "TVS102";

    /// <summary>The filename mixes separators.</summary>
    public const string FilenameSeparators = "TVS103";

    /// <summary>The filename repeats a separator.</summary>
    public const string FilenameRepeatedSeparators = "TVS104";

    /// <summary>The filename has dots in its stem.</summary>
    public const string FilenameStrayDots = "TVS105";

    /// <summary>The filename starts with an <c>@</c> handle.</summary>
    public const string FilenameHandlePrefix = "TVS106";

    /// <summary>The filename breaks no rule but has a simpler form.</summary>
    public const string FilenameSimplify = "TVS107";

    /// <summary>The path cannot be written on Windows.</summary>
    public const string FilenameWindowsPortability = "TVS108";

    /// <summary>The note duplicates another.</summary>
    public const string DuplicateNote = "TVS201";
}

/// <summary>One filename rule a stem breaks.</summary>
/// <param name="Code">The rule's code, one of the <c>TVS1xx</c> <see cref="LintCodes"/>.</param>
/// <param name="Reason">What is wrong, for the report.</param>
/// <param name="Severity">Always <c>warning</c> today.</param>
public sealed record FilenameIssueSpec(string Code, string Reason, string Severity);

/// <summary>The vault lint's filename and type rules, ported from <c>rig.obsidian.lint_rules</c>.</summary>
public static partial class LintRules
{
    private const string Warning = "warning";
    private const string NotPortable = "path is not portable to Windows";

    private static readonly HashSet<string> WindowsReservedNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).Select(i => $"COM{i}"))
            .Concat(Enumerable.Range(1, 9).Select(i => $"LPT{i}")),
        StringComparer.Ordinal);

    private static readonly string[] HubStems = ["Resources", "Areas", "Projects", "Archive"];
    private static readonly string[] HubRoots = ["Resources", "Areas", "Projects"];

    /// <summary>Why <paramref name="path"/> cannot be written on Windows, or <see langword="null"/> when it can.</summary>
    public static string? WindowsPortabilityIssueReason(NotePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (var part in path.Parts)
        {
            if (WindowsReservedChars().IsMatch(part) || part.EndsWith(' ') || part.EndsWith('.'))
            {
                return NotPortable;
            }

            var dot = part.IndexOf('.', StringComparison.Ordinal);
            // The reserved names are ASCII, and no non-ASCII character uppercases into their letters or digits.
            if (WindowsReservedNames.Contains((dot >= 0 ? part[..dot] : part).ToUpperInvariant()))
            {
                return NotPortable;
            }
        }

        return null;
    }

    /// <summary>A lowercase stem that is safe on Windows.</summary>
    public static string PortableWindowsStem(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        return VaultText.SanitizeFilename(PyText.Lower(stem));
    }

    /// <summary>Every filename rule <paramref name="stem"/> breaks; the simplify rule only when it breaks no other.</summary>
    public static IReadOnlyList<FilenameIssueSpec> FilenameIssueSpecs(string stem, string suggestedStem, NotePath? path = null)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var found = new List<FilenameIssueSpec>();
        if (VaultText.LooksLikeUrlishStem(stem) || StatusToken().IsMatch(PyText.Lower(stem)))
        {
            found.Add(new(LintCodes.FilenameUrlish, "filename is URL-shaped", Warning));
        }

        if (!stem.All(char.IsAscii))
        {
            found.Add(new(LintCodes.FilenameNonAscii, "filename includes non-ASCII characters", Warning));
        }

        if (stem.Contains("  ", StringComparison.Ordinal) || stem.Contains('_', StringComparison.Ordinal))
        {
            found.Add(new(LintCodes.FilenameSeparators, "filename uses inconsistent separators", Warning));
        }

        if (VaultText.RepeatedHyphens().IsMatch(stem))
        {
            found.Add(new(LintCodes.FilenameRepeatedSeparators, "filename has repeated separators", Warning));
        }

        if (stem.Contains('.', StringComparison.Ordinal))
        {
            found.Add(new(LintCodes.FilenameStrayDots, "filename includes stray dots", Warning));
        }

        if (stem.StartsWith('@'))
        {
            found.Add(new(LintCodes.FilenameHandlePrefix, "filename includes handle-style prefix", Warning));
        }

        var portability = path is null ? null : WindowsPortabilityIssueReason(path);
        if (portability is not null)
        {
            found.Add(new(LintCodes.FilenameWindowsPortability, portability, Warning));
        }

        if (found.Count == 0 && !string.Equals(stem, suggestedStem, StringComparison.Ordinal))
        {
            found.Add(new(LintCodes.FilenameSimplify, "filename can be simplified", Warning));
        }

        return found;
    }

    /// <summary>Whether <paramref name="stem"/> breaks a filename rule the cleanup command fixes.</summary>
    public static bool NeedsFilenameCleanup(string stem, NotePath? path = null)
    {
        ArgumentNullException.ThrowIfNull(stem);
        var lower = PyText.Lower(stem);
        return VaultText.LooksLikeUrlishStem(lower)
            || !stem.All(char.IsAscii)
            || stem.Contains('_', StringComparison.Ordinal)
            || stem.Contains("  ", StringComparison.Ordinal)
            || VaultText.RepeatedHyphens().IsMatch(stem)
            || stem.Contains('.', StringComparison.Ordinal)
            || DomainToken().IsMatch(lower)
            || stem.StartsWith('@')
            || (path is not null && WindowsPortabilityIssueReason(path) is not null);
    }

    /// <summary>The type a note's location and name suggest, or <see langword="null"/> when they suggest none.</summary>
    public static string? SuggestedType(NotePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var text = path.ToString();
        var stem = path.Stem;
        var parent = path.ParentName;
        var isHub = HubStems.Contains(stem, StringComparer.Ordinal)
            || stem.Contains("Index", StringComparison.Ordinal)
            || stem.Contains("Hub", StringComparison.Ordinal)
            || stem.Contains("Inbox-", StringComparison.Ordinal)
            || string.Equals(parent, stem, StringComparison.Ordinal)
            || string.Equals(parent + " Index", stem, StringComparison.Ordinal)
            || (path.Parts.Count == 2 && HubRoots.Contains(path.Parts[0], StringComparer.Ordinal));

        if (isHub)
        {
            return Has(text, "Resources") || Has(text, "Social Media") ? "resource-hub"
                : Has(text, "Projects") ? "project"
                : "area";
        }

        return Has(text, "Social Media Posts") ? "social"
            : Has(text, "Resources") ? "resource"
            : Has(text, "Projects") ? (Has(text, "Design") ? "design-note" : "project-note")
            : Has(text, "Areas") ? "area"
            : null;
    }

    private static bool Has(string text, string value) => text.Contains(value, StringComparison.Ordinal);

    [GeneratedRegex("""[<>:"/\\|?*]""")]
    private static partial Regex WindowsReservedChars();

    [GeneratedRegex(@"(?:^|[-_ ])status[-_ ]\d+")]
    private static partial Regex StatusToken();

    [GeneratedRegex(@"(?:^|[-_ ])(?:[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|org|net|io|dev|ai|app|co|uk|eu|fm|me|tv|ly|sh|rs))(?:$|[ .:_-])")]
    private static partial Regex DomainToken();
}

/// <summary>One folder's finding counts, tallied as findings arrive, then scored by <see cref="Rescore"/>.</summary>
public sealed class FolderLintStats
{
    /// <summary>Metadata errors in the folder.</summary>
    public int MetadataErrors { get; set; }

    /// <summary>Metadata warnings in the folder.</summary>
    public int MetadataWarnings { get; set; }

    /// <summary>Filename issues in the folder.</summary>
    public int FilenameIssues { get; set; }

    /// <summary>Duplicate notes in the folder.</summary>
    public int Duplicates { get; set; }

    /// <summary>Notes in the folder.</summary>
    public int TotalNotes { get; set; }

    /// <summary>The folder's score, 0 to 100; 100 until <see cref="Rescore"/> runs.</summary>
    public double Score { get; private set; } = 100.0;

    /// <summary>Scores the folder: weighted findings per note, less 0.5 per note past 12 for overcrowding, never below 0.</summary>
    public void Rescore()
    {
        var total = TotalNotes;
        if (total == 0)
        {
            Score = 100.0;
            return;
        }

        var penalty = (MetadataErrors * 5) + (MetadataWarnings * 2) + (FilenameIssues * 2) + (Duplicates * 10);
        var lintScore = 100.0 - ((double)penalty / total);
        var sizePenalty = total > 12 ? (total - 12) * 0.5 : 0.0;
        Score = Math.Max(0.0, lintScore - sizePenalty);
    }
}
