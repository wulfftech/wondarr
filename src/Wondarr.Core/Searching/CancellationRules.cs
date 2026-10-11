namespace Wondarr.Core.Searching;

/// <summary>
/// The one rule for telling a timeout from a shutdown. .NET reports an <c>HttpClient</c> timeout as a
/// <see cref="TaskCanceledException"/>, which is an <see cref="OperationCanceledException"/>, so the
/// exception type alone cannot say whether the caller gave up or the request ran out of time.
/// </summary>
public static class CancellationRules
{
    /// <summary>
    /// Whether <paramref name="exception"/> means "stop": an <see cref="OperationCanceledException"/>
    /// while the caller's own token is cancelled. Any other <see cref="OperationCanceledException"/> is
    /// a timeout, and the request failed like any other.
    /// </summary>
    /// <param name="exception">What was thrown.</param>
    /// <param name="cancellationToken">The token of the caller that would have to be stopping.</param>
    public static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }
}
