using System.Globalization;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Separates the evidence of a failed outbound HTTP call from the private text that comes
/// with it.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="HttpRequestException"/> message, and the
/// message of whatever wrapped it, can carry the remote service's response text and the
/// request URL: a person's name, an account number in a path, a token in a query string.
/// Logged whole, that sits in the log store for the retention period.
/// </para>
/// <para>
/// The type, the HTTP status and the stack frames are what a person needs to find the fault,
/// and none of them is private. <see cref="Snapshot"/> keeps those and drops the rest.
/// </para>
/// </remarks>
public static class TransportExceptionLog
{
    /// <summary>
    /// Whether an exception is, or wraps, an <see cref="HttpRequestException"/>.
    /// </summary>
    /// <param name="exception">The exception, which may be null.</param>
    /// <returns>
    /// True when the exception, one of its inner exceptions, or one inside an
    /// <see cref="AggregateException"/> is an <see cref="HttpRequestException"/>.
    /// </returns>
    public static bool IsTransportFailure(Exception? exception) => FindHttpFailure(exception) is not null;

    /// <summary>
    /// The exception to hand to the logger in place of the original.
    /// </summary>
    /// <remarks>
    /// The stand-in's message names the outermost exception's type and the HTTP status, or
    /// <c>unavailable</c> when the call got no response. Its stack trace is the outermost
    /// exception's, or the HTTP failure's when the outermost was never thrown. It has no inner
    /// exception, because every message in the chain can carry the private text.
    /// </remarks>
    /// <param name="exception">The exception that was caught, which may be null.</param>
    /// <returns>
    /// A stand-in when <paramref name="exception"/> is a transport failure, and
    /// <paramref name="exception"/> itself, untouched, when it is not.
    /// </returns>
    public static Exception? Snapshot(Exception? exception)
    {
        var httpFailure = FindHttpFailure(exception);

        return exception is null || httpFailure is null
            ? exception
            : new TransportFailureSnapshot(exception, httpFailure);
    }

    private static HttpRequestException? FindHttpFailure(Exception? exception)
    {
        if (exception is HttpRequestException httpFailure)
        {
            return httpFailure;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions
                .Select(FindHttpFailure)
                .FirstOrDefault(failure => failure is not null);
        }

        return exception?.InnerException is null ? null : FindHttpFailure(exception.InnerException);
    }

    private sealed class TransportFailureSnapshot(Exception original, HttpRequestException httpFailure)
        : Exception(Describe(original, httpFailure))
    {
        private readonly string? _stackTrace = original.StackTrace ?? httpFailure.StackTrace;

        public override string? StackTrace => _stackTrace;

        private static string Describe(Exception original, HttpRequestException httpFailure)
        {
            var status = httpFailure.StatusCode is { } code
                ? ((int)code).ToString(CultureInfo.InvariantCulture)
                : "unavailable";

            return $"{original.GetType().FullName}: outbound HTTP request failed; status {status}. "
                + "Response text and request URLs are omitted from logs.";
        }
    }
}
