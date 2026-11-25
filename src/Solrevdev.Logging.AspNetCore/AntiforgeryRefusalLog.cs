using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Writes <see cref="RefusalEvents.WebAntiforgeryRefused"/>, for any code that learns a request
/// failed its anti-forgery check.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AntiforgeryRefusalLogFilter"/> covers MVC and Razor Pages and calls this, so
/// there is one implementation of the event. A minimal-API endpoint is outside MVC and no
/// global MVC filter sees it. Without this, each such endpoint has to copy the cookie test,
/// the user agent cut and the templates to log the same event; this is that code, once.
/// </para>
/// <para>
/// From an endpoint that validates for itself:
/// </para>
/// <code>
/// try
/// {
///     await antiforgery.ValidateRequestAsync(httpContext);
/// }
/// catch (AntiforgeryValidationException exception)
/// {
///     AntiforgeryRefusalLog.Write(logger, httpContext, exception);
///     return Results.BadRequest();
/// }
/// </code>
/// <para>
/// For a route group, <see cref="AntiforgeryRefusalEndpointFilter"/> does it without a catch
/// in each endpoint.
/// </para>
/// <para>
/// The event is a Warning and carries <see cref="LoggingContract.RequestMethod"/>,
/// <see cref="LoggingContract.RequestPath"/>, <see cref="LoggingContract.RefusalReason"/>,
/// <see cref="LoggingContract.HasAntiforgeryCookie"/> and
/// <see cref="LoggingContract.UserAgent"/>. It is written at most once per request, whoever
/// asks, so a filter and a catch block on one endpoint do not count one refusal twice.
/// </para>
/// </remarks>
public static class AntiforgeryRefusalLog
{
    /// <summary>
    /// The <see cref="LoggingContract.RefusalReason"/> when nothing said why.
    /// </summary>
    public const string UnknownReason = "unknown";

    // The framework's default cookie name is this plus a hash of the application name.
    private const string DefaultCookiePrefix = ".AspNetCore.Antiforgery.";

    // Templates are built from the contract constants, so no name is typed twice.
    private const string Head =
        "{" + LoggingContract.Event + "} {" + LoggingContract.RequestMethod + "} {"
        + LoggingContract.RequestPath + "} refused: {" + LoggingContract.RefusalReason
        + "}; anti-forgery cookie sent {" + LoggingContract.HasAntiforgeryCookie + "}";

    private const string WithUserAgent = Head + ", from {" + LoggingContract.UserAgent + "}";

    private static readonly object WrittenKey = new();

    private static readonly RequestLogOptions Fallback = new();

    /// <summary>
    /// Writes the event for a request that failed its anti-forgery check.
    /// </summary>
    /// <remarks>
    /// The user agent is cut to <see cref="RequestLogOptions.MaximumUserAgentLength"/>, read
    /// from the request services, or to the package default when there are none. It is absent
    /// when the caller sent none.
    /// </remarks>
    /// <param name="logger">The logger to write with. Its category is the caller's choice.</param>
    /// <param name="httpContext">The refused request.</param>
    /// <param name="exception">
    /// The exception the check threw. Its message becomes
    /// <see cref="LoggingContract.RefusalReason"/>: the framework's messages name the missing
    /// cookie, header or field and never a token value. Null writes
    /// <see cref="UnknownReason"/>.
    /// </param>
    /// <returns>
    /// True when the event was written, false when it had already been written for this
    /// request.
    /// </returns>
    public static bool Write(
        ILogger logger,
        HttpContext httpContext,
        AntiforgeryValidationException? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(httpContext);

        var options = httpContext.RequestServices?.GetService<IOptions<RequestLogOptions>>()?.Value ?? Fallback;

        return WriteCut(logger, httpContext, exception, options.MaximumUserAgentLength);
    }

    /// <summary>
    /// The one place the event is written. The cap is passed in so the MVC filter can use the
    /// options it was built with.
    /// </summary>
    internal static bool WriteCut(
        ILogger logger,
        HttpContext httpContext,
        AntiforgeryValidationException? exception,
        int maximumUserAgentLength)
    {
        if (!httpContext.Items.TryAdd(WrittenKey, true))
        {
            return false;
        }

        var request = httpContext.Request;
        var services = httpContext.RequestServices;
        var reason = string.IsNullOrWhiteSpace(exception?.Message) ? UnknownReason : exception.Message;

        // An application can rename the cookie, so the configured name counts as well.
        var configuredName = services?.GetService<IOptions<AntiforgeryOptions>>()?.Value.Cookie.Name;
        var hasAntiforgeryCookie = request.Cookies.Keys.Any(name =>
            name.StartsWith(DefaultCookiePrefix, StringComparison.Ordinal)
            || (configuredName is not null && string.Equals(name, configuredName, StringComparison.Ordinal)));

        var userAgent = HeaderValue.Truncate(request.Headers.UserAgent.ToString(), maximumUserAgentLength);

        // Absent, never blank: a caller that sent no user agent gets no UserAgent property.
        if (userAgent is null)
        {
            logger.LogWarning(
                Head,
                RefusalEvents.WebAntiforgeryRefused,
                request.Method,
                request.Path.Value,
                reason,
                hasAntiforgeryCookie);
        }
        else
        {
            logger.LogWarning(
                WithUserAgent,
                RefusalEvents.WebAntiforgeryRefused,
                request.Method,
                request.Path.Value,
                reason,
                hasAntiforgeryCookie,
                userAgent);
        }

        return true;
    }

    /// <summary>
    /// Runs the anti-forgery check again to read why it failed, then writes the event.
    /// </summary>
    /// <remarks>
    /// For code that knows the check failed and was not handed the exception, which is where
    /// the MVC filter stands. Validating again costs one cookie parse, on the failure path
    /// only. With no <see cref="IAntiforgery"/> in the request services, or if the second
    /// check passes, the reason is <see cref="UnknownReason"/>.
    /// </remarks>
    /// <param name="logger">The logger to write with.</param>
    /// <param name="httpContext">The refused request.</param>
    /// <returns>
    /// True when the event was written, false when it had already been written for this
    /// request.
    /// </returns>
    public static async Task<bool> WriteAsync(ILogger logger, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(httpContext);

        var failure = await ReasonAsync(httpContext).ConfigureAwait(false);

        return Write(logger, httpContext, failure);
    }

    /// <summary>
    /// Runs the check again and returns what it threw, or null when it cannot say.
    /// </summary>
    internal static async Task<AntiforgeryValidationException?> ReasonAsync(HttpContext httpContext)
    {
        AntiforgeryValidationException? failure = null;

        if (httpContext.RequestServices?.GetService<IAntiforgery>() is { } antiforgery)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(httpContext).ConfigureAwait(false);
            }
            catch (AntiforgeryValidationException exception)
            {
                failure = exception;
            }
        }

        return failure;
    }
}

/// <summary>
/// Writes <see cref="RefusalEvents.WebAntiforgeryRefused"/> for minimal-API endpoints, as an
/// endpoint filter on a route or a route group.
/// </summary>
/// <remarks>
/// <para>
/// It logs when the endpoint, or a filter inside this one, throws
/// <see cref="AntiforgeryValidationException"/>, as a handler that calls
/// <c>IAntiforgery.ValidateRequestAsync</c> does. The event is written and the exception is
/// thrown on, so the response is whatever it was going to be.
/// </para>
/// <code>
/// var orders = app.MapGroup("/orders").AddEndpointFilter&lt;AntiforgeryRefusalEndpointFilter&gt;();
/// </code>
/// <para>
/// It needs no registration: the framework builds it from the application's services.
/// </para>
/// <para>
/// <strong>Two cases it cannot see, both measured.</strong> An endpoint that catches the
/// exception itself must call <see cref="AntiforgeryRefusalLog.Write"/> in the catch. And an
/// endpoint that binds a form and leaves the check to <c>UseAntiforgery</c> is answered 400
/// by the framework before any endpoint filter runs; a test pins that this filter logs
/// nothing there. For that case add a few lines of middleware after <c>UseAntiforgery</c>:
/// </para>
/// <code>
/// app.UseAntiforgery();
/// app.Use(async (httpContext, next) =>
/// {
///     if (httpContext.Features.Get&lt;IAntiforgeryValidationFeature&gt;() is { IsValid: false } refused)
///     {
///         AntiforgeryRefusalLog.Write(logger, httpContext, refused.Error as AntiforgeryValidationException);
///     }
///
///     await next(httpContext);
/// });
/// </code>
/// <para>
/// That middleware is not shipped as a <c>Use</c> method on purpose. The feature is set for
/// every endpoint the middleware checks, and whether an invalid one is then refused depends
/// on the endpoint, so only the application knows that logging it there is right.
/// </para>
/// </remarks>
/// <param name="logger">The logger.</param>
public sealed class AntiforgeryRefusalEndpointFilter(ILogger<AntiforgeryRefusalEndpointFilter> logger) : IEndpointFilter
{
    private readonly ILogger<AntiforgeryRefusalEndpointFilter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Runs the endpoint, and writes the event if an anti-forgery failure comes out of it.
    /// </summary>
    /// <param name="context">The endpoint invocation.</param>
    /// <param name="next">The next filter or the endpoint.</param>
    /// <returns>What the endpoint returned.</returns>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException exception)
        {
            AntiforgeryRefusalLog.Write(_logger, context.HttpContext, exception);
            throw;
        }
    }
}
