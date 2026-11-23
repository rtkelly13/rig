
namespace Rig.Core.Markup;

/// <summary>A value of a YAML document as the vault tools read it.</summary>
public abstract record YamlValue;

/// <summary>A null.</summary>
public sealed record YamlNull : YamlValue
{
    /// <summary>The one null.</summary>
    public static YamlNull Value { get; } = new();
}

/// <summary>A string.</summary>
/// <param name="Text">Its text.</param>
public sealed record YamlString(string Text) : YamlValue;

/// <summary>
/// A scalar that is not a string or null: an int, float, bool, date or datetime. <paramref name="Rendered"/> is its canonical text.
/// </summary>
/// <param name="Rendered">The value as text.</param>
public sealed record YamlScalar(string Rendered) : YamlValue;

/// <summary>A sequence.</summary>
/// <param name="Items">Its items, in order.</param>
public sealed record YamlList(IReadOnlyList<YamlValue> Items) : YamlValue;

/// <summary>A mapping, its keys unique and in insertion order.</summary>
/// <param name="Entries">Its entries, in insertion order.</param>
public sealed record YamlMap(IReadOnlyList<KeyValuePair<YamlValue, YamlValue>> Entries) : YamlValue;

