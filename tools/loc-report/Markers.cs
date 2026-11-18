using System.Text.RegularExpressions;

namespace Rig.LocReport;

/// <summary>Every Python carry-over the report looks for. Extend these lists; nothing else needs to change.</summary>
internal static class Markers
{
    /// <summary>Patterns counted as occurrences across <c>src/**/*.cs</c>, with the label each is reported under.</summary>
    public static readonly IReadOnlyList<(string Label, Regex Pattern)> Words =
    [
        ("the word Python", new Regex(@"\bPython\b", RegexOptions.CultureInvariant)),
        ("SnakeCase uses", new Regex("SnakeCase", RegexOptions.CultureInvariant)),
    ];

    /// <summary>Helpers kept to match Python's output; their file's line count is reported (file name without extension).</summary>
    public static readonly IReadOnlyList<string> Helpers =
    [
        "VaultYaml",
        "VaultUrl",
        "VaultText",
        "YamlNumbers",
        "YamlValue",
        "QuotedText",
    ];
}
