namespace RaiCore.Pdf;

/// <summary>
/// Thrown when input exceeds one of the <see cref="ResourceLimits"/>: it is nested too deeply, is too large, decodes to
/// too much data, makes too many pages, or takes too long to convert.
/// </summary>
public sealed class ResourceLimitExceededException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ResourceLimitExceededException()
        : this(string.Empty, 0, "A resource limit was exceeded.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ResourceLimitExceededException(string message)
        : this(string.Empty, 0, message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public ResourceLimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
        LimitName = string.Empty;
    }

    /// <summary>Creates the exception for a limit.</summary>
    /// <param name="limitName">The name of the <see cref="ResourceLimits"/> property, such as <c>MaxPages</c>.</param>
    /// <param name="limit">The limit's value (for <see cref="ResourceLimits.Timeout"/>, in milliseconds).</param>
    /// <param name="message">The message.</param>
    public ResourceLimitExceededException(string limitName, long limit, string message)
        : base(message)
    {
        LimitName = limitName;
        Limit = limit;
    }

    /// <summary>The name of the <see cref="ResourceLimits"/> property that was exceeded, such as <c>MaxPages</c>.</summary>
    public string LimitName { get; }

    /// <summary>The limit's value (for <see cref="ResourceLimits.Timeout"/>, in milliseconds).</summary>
    public long Limit { get; }
}
