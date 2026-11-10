namespace Rig.Core.Processes;

/// <summary>
/// The one way rig runs other programs. Nothing throws for a failed or missing command: the <see cref="Ran"/>
/// says what happened, and <see cref="Ran.EnsureOk"/> turns failure into an exception where a caller needs it.
/// </summary>
public interface IProc
{
    /// <summary>Runs <paramref name="request"/> and captures its output.</summary>
    Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default);
}
