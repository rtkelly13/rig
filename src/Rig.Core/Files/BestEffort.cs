namespace Rig.Core.Files;

/// <summary>
/// Cleanup and rollback steps whose failure must not replace the outcome already decided: a leftover temp file or a
/// link that could not be restored is better than losing the result or the original exception.
/// </summary>
public static class BestEffort
{
    /// <summary>Runs <paramref name="step"/>, dropping a filesystem refusal; any other failure still propagates.</summary>
    /// <returns>Whether the step completed.</returns>
    public static bool Io(Action step)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            step();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
