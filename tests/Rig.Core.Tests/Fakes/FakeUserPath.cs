using Rig.Core.Runtime;

namespace Rig.Core.Tests.Fakes;

/// <summary>A User PATH held in memory, recording every write; <see cref="Failure"/> makes reads throw.</summary>
/// <param name="value">The value it starts with, or null for none.</param>
public sealed class FakeUserPath(string? value = null) : IUserPath
{
    /// <summary>The current value.</summary>
    public string? Value { get; private set; } = value;

    /// <summary>Every value written, in order.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Thrown by <see cref="Read"/> when set, as a locked-down registry would.</summary>
    public Exception? Failure { get; init; }

    /// <inheritdoc/>
    public string? Read() => Failure is { } failure ? throw failure : Value;

    /// <inheritdoc/>
    public void Write(string value)
    {
        Value = value;
        Writes.Add(value);
    }
}
