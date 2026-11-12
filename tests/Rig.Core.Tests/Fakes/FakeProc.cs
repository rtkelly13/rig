using Rig.Core.Processes;

namespace Rig.Core.Tests.Fakes;

/// <summary>An <see cref="IProc"/> that records every request and answers from a table keyed by argv prefix.</summary>
public sealed class FakeProc : IProc
{
    private readonly List<(string? WorkingDirectory, IReadOnlyList<string> Prefix, Ran Answer)> _answers = [];

    /// <summary>Every request, in the order it was made.</summary>
    public List<ProcRequest> Requests { get; } = [];

    /// <summary>What an unanswered command gets.</summary>
    public Ran Unanswered { get; set; } = new(Ran.NotFound, string.Empty, "no fake answer");

    /// <summary>Answers any command starting with <paramref name="prefix"/>; later answers win.</summary>
    public FakeProc Answer(Ran answer, params string[] prefix)
    {
        _answers.Insert(0, (null, prefix, answer));
        return this;
    }

    /// <summary>Answers a command starting with <paramref name="prefix"/> run in <paramref name="workingDirectory"/>; later answers win.</summary>
    public FakeProc AnswerIn(string workingDirectory, Ran answer, params string[] prefix)
    {
        _answers.Insert(0, (workingDirectory, prefix, answer));
        return this;
    }

    /// <inheritdoc />
    public Ran Run(ProcRequest request)
    {
        Requests.Add(request);
        var match = _answers.FirstOrDefault(a =>
            (a.WorkingDirectory is null || a.WorkingDirectory == request.WorkingDirectory) && request.Argv.Take(a.Prefix.Count).SequenceEqual(a.Prefix));
        return match.Answer ?? Unanswered;
    }

    /// <inheritdoc />
    public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Run(request));
}
