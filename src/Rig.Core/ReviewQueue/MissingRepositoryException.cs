namespace Rig.Core.ReviewQueue;

/// <summary>GitHub answered that a managed repository does not resolve: deleted, renamed or not visible to this login.</summary>
public sealed class MissingRepositoryException : Exception
{
    /// <summary>An unnamed repository did not resolve.</summary>
    public MissingRepositoryException()
    {
    }

    /// <summary>A failure with <paramref name="message"/>.</summary>
    public MissingRepositoryException(string message)
        : base(message)
    {
    }

    /// <summary>A failure with <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public MissingRepositoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
