using Rig.Core.Desktop;

namespace Rig.Core.Tests.Fakes;

/// <summary>A run log that keeps every line in memory, failures with their exception's message.</summary>
public sealed class FakeRunLog : IRunLog
{
    /// <summary>Every line logged, in order.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>Every failure logged, with the exception that came with it.</summary>
    public List<(string Message, Exception Failure)> Failures { get; } = [];

    /// <inheritdoc/>
    public void Log(string message) => Lines.Add(message);

    /// <inheritdoc/>
    public void LogFailure(string message, Exception failure)
    {
        Lines.Add(message);
        Failures.Add((message, failure));
    }
}
