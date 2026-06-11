namespace GroupWeaver.Core.Providers;

/// <summary>
/// Thrown when the directory cannot be reached or the bind fails. Unresolvable DNs
/// are NOT this exception — they are values (null / <c>External</c> / empty list).
/// </summary>
public sealed class DirectoryUnavailableException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public DirectoryUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public DirectoryUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
