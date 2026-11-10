namespace Rig.Core.Vault;

/// <summary>
/// A note's path relative to the vault, always with forward slashes, read as Python's <c>PurePosixPath</c> reads it on
/// every platform: empty and <c>.</c> segments dropped, and the stem the name before its last dot unless that dot leads
/// or ends the name.
/// </summary>
public sealed class NotePath
{
    private NotePath(IReadOnlyList<string> parts) => Parts = parts;

    /// <summary>The path's segments, in order.</summary>
    public IReadOnlyList<string> Parts { get; }

    /// <summary>The last segment, or empty for an empty path.</summary>
    public string Name => Parts.Count > 0 ? Parts[^1] : string.Empty;

    /// <summary>The name without its suffix: <c>Note</c> for <c>Note.md</c>, but <c>.md</c> and <c>foo.</c> as they are.</summary>
    public string Stem
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot > 0 && dot < name.Length - 1 ? name[..dot] : name;
        }
    }

    /// <summary>The name of the folder holding the note, or empty at the vault root.</summary>
    public string ParentName => Parts.Count > 1 ? Parts[^2] : string.Empty;

    /// <summary>Reads <paramref name="relative"/>, a vault-relative path with forward slashes.</summary>
    public static NotePath Parse(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return new NotePath([.. relative.Split('/').Where(part => part.Length > 0 && part != ".")]);
    }

    /// <summary>The segments joined with forward slashes, as <c>str(PurePosixPath)</c> gives them.</summary>
    public override string ToString() => Parts.Count > 0 ? string.Join('/', Parts) : ".";
}
