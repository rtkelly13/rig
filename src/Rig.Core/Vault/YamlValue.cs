namespace Rig.Core.Vault;

/// <summary>A value of a YAML document as the YAML 1.1 safe schema builds it, kept only as far as the vault tools look at it.</summary>
public abstract record YamlValue;

/// <summary>A null.</summary>
public sealed record YamlNull : YamlValue
{
    /// <summary>The one <c>None</c>.</summary>
    public static YamlNull Value { get; } = new();
}

/// <summary>A string.</summary>
/// <param name="Text">Its text.</param>
public sealed record YamlString(string Text) : YamlValue;

/// <summary>
/// A scalar that is not a string or <c>None</c>: an int, float, bool, date or datetime. <paramref name="Rendered"/> is what
/// the YAML 1.1 safe schema renders it as.
/// </summary>
/// <param name="Rendered">The value as text.</param>
public sealed record YamlScalar(string Rendered) : YamlValue;

/// <summary>A sequence.</summary>
/// <param name="Items">Its items, in order.</param>
public sealed record YamlList(IReadOnlyList<YamlValue> Items) : YamlValue;

/// <summary>A mapping, its keys unique and in insertion order.</summary>
/// <param name="Entries">Its entries, in insertion order.</param>
public sealed record YamlMap(IReadOnlyList<KeyValuePair<YamlValue, YamlValue>> Entries) : YamlValue;

/// <summary>Anything else the loader builds (bytes, a set, a tuple), which the vault tools never read into.</summary>
public sealed record YamlOther : YamlValue
{
    /// <summary>The one placeholder.</summary>
    public static YamlOther Value { get; } = new();
}
