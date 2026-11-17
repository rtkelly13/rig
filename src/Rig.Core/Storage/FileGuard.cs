namespace Rig.Core.Storage;

/// <summary>Runs a file-system read that may fail for ordinary reasons (missing, locked, denied) and answers a fallback instead.</summary>
public static class FileGuard
{
    /// <summary>The result of <paramref name="read"/>, or <paramref name="fallback"/> when it throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
    public static T Try<T>(Func<T> read, T fallback)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }
}
