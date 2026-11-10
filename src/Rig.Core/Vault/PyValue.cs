namespace Rig.Core.Vault;

/// <summary>A value as PyYAML's safe loader would hand it to Python, kept only as far as the vault tools look at it.</summary>
public abstract record PyValue;

/// <summary>Python's <c>None</c>.</summary>
public sealed record PyNone : PyValue
{
    /// <summary>The one <c>None</c>.</summary>
    public static PyNone Value { get; } = new();
}

/// <summary>A Python <c>str</c>.</summary>
/// <param name="Text">Its text.</param>
public sealed record PyStr(string Text) : PyValue;

/// <summary>
/// A scalar that is not a string or <c>None</c>: an int, float, bool, date or datetime. <paramref name="Rendered"/> is what
/// Python's <c>str()</c> makes of it.
/// </summary>
/// <param name="Rendered">Python's <c>str()</c> of the value.</param>
public sealed record PyScalar(string Rendered) : PyValue;

/// <summary>A Python <c>list</c>.</summary>
/// <param name="Items">Its items, in order.</param>
public sealed record PyList(IReadOnlyList<PyValue> Items) : PyValue;

/// <summary>A Python <c>dict</c>, its keys unique and in insertion order.</summary>
/// <param name="Entries">Its entries, in insertion order.</param>
public sealed record PyDict(IReadOnlyList<KeyValuePair<PyValue, PyValue>> Entries) : PyValue;

/// <summary>Anything else the loader builds (bytes, a set, a tuple), which the vault tools never read into.</summary>
public sealed record PyOther : PyValue
{
    /// <summary>The one placeholder.</summary>
    public static PyOther Value { get; } = new();
}
