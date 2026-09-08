using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Serilog;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Adds the caller's context, and the user where there is one, to Serilog's request summary.
/// </summary>
/// <remarks>
/// <para>
/// The summary is written by <c>UseSerilogRequestLogging</c> after the rest of the pipeline
/// has returned, so every <c>LogContext</c> scope pushed BELOW it has already been disposed.
/// In the documented order that is <see cref="UserLogContextMiddleware"/>: the user is gone by
/// the time the summary is written, which is why this reads the principal off the
/// <see cref="HttpContext"/> instead.
/// </para>
/// <para>
/// <see cref="ClientLogContextMiddleware"/> sits ABOVE request logging in that same order, so
/// its scope is still open and its two properties would land on the summary anyway. They are
/// set here regardless, and deliberately: an application is free to wire the summary without
/// the client middleware, and a property that appears only when two components are both
/// present is the drift this package exists to end. Setting the same value twice costs
/// nothing.
/// </para>
/// <para>
/// Nothing here may DEPEND on <c>LogContext</c>. A property pushed by inner middleware and
/// only ever read on the summary event would never land, and would fail silently: the query
/// returns nothing and looks like quiet traffic.
/// </para>
/// </remarks>
public static class RequestSummaryEnricher
{
    private static readonly RequestLogOptions Fallback = new();

    /// <summary>
    /// The delegate handed to <c>options.EnrichDiagnosticContext</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Options are read from the request services, so an application that called
    /// <c>AddSolrevdevRequestLogging</c> gets its own configuration. Prefer
    /// <see cref="SerilogRequestLoggingExtensions.UseSolrevdevRequestLogging"/>, which wires
    /// this for you.
    /// </para>
    /// <para>
    /// <strong>It falls back to the package defaults, silently, when the options cannot be
    /// resolved.</strong> That is either a null <c>HttpContext.RequestServices</c> or an
    /// application that wired the middleware without calling
    /// <c>AddSolrevdevRequestLogging</c>. It matters most in a test: a bare
    /// <c>DefaultHttpContext</c> has no <c>RequestServices</c>, so a test using this overload
    /// measures the package defaults while appearing to measure the application's own caps and
    /// selectors. Give the context a <c>RequestServices</c> built from the application's
    /// registrations, or call
    /// <see cref="Enrich(IDiagnosticContext, HttpContext, RequestLogOptions)"/> with the
    /// options in hand.
    /// </para>
    /// <para>
    /// The other way to reach the fallback, an application that wired the middleware without
    /// calling <c>AddSolrevdevRequestLogging</c>, is refused at startup by
    /// <see cref="RequestLoggingMarkerService"/>. That guard covers the application. It cannot
    /// cover the test above, because no application starts there.
    /// </para>
    /// </remarks>
    /// <param name="diagnosticContext">The <see cref="IDiagnosticContext"/> for the request.</param>
    /// <param name="httpContext">The <see cref="HttpContext"/>.</param>
    public static void EnrichRequestSummary(IDiagnosticContext diagnosticContext, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var options = httpContext.RequestServices?
            .GetService<IOptions<RequestLogOptions>>()?.Value ?? Fallback;

        Enrich(diagnosticContext, httpContext, options);
    }

    /// <summary>
    /// Adds the caller's context to the request summary against explicit options.
    /// </summary>
    /// <remarks>
    /// Client context first, and OUTSIDE the authentication check. An earlier version started
    /// with that check and returned, so an anonymous request got no enrichment at all, which
    /// is backwards: the login page, a rate-limited authentication POST, a 404 and a probe are
    /// the requests you most want an address and a user agent on, and they are precisely the
    /// ones that have no user.
    /// </remarks>
    /// <param name="diagnosticContext">The <see cref="IDiagnosticContext"/> for the request.</param>
    /// <param name="httpContext">The <see cref="HttpContext"/>.</param>
    /// <param name="options">The options.</param>
    public static void Enrich(
        IDiagnosticContext diagnosticContext,
        HttpContext httpContext,
        RequestLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(diagnosticContext);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(options);

        Set(diagnosticContext, LoggingContract.TraceId, Activity.Current?.TraceId.ToString());

        var clientIp = ClientAddress.Describe(httpContext.Connection?.RemoteIpAddress);
        Set(diagnosticContext, LoggingContract.ClientIp, clientIp);

        // A best-effort reconstruction of what the caller CLAIMED, kept next to what the
        // framework SETTLED ON above. A value here that does not match ClientIp is worth
        // looking at, and is not by itself proof of anything: the trusted networks may not
        // cover the balancer, somebody may have written an extra entry to name themselves, or
        // the reconstruction may simply have lost a port or a hop. ForwardedForHeader.Claimed
        // lists what it loses.
        //
        // Deliberately NOT through ClientAddress. This property is the claim rather than the
        // settled address, and reshaping a claim before recording it makes it worth even less.
        Set(
            diagnosticContext,
            LoggingContract.ForwardedFor,
            ForwardedForHeader.Claimed(
                httpContext.Request.Headers,
                httpContext.Connection?.RemoteIpAddress?.ToString(),
                options.MaximumForwardedForLength));

        // Scheme, host and path only. A referer sent from the password-reset or
        // email-confirmation page carries a one-time token in its query string, so logging
        // the referer whole would write a live credential into the log store and leave it
        // there for the retention period.
        Set(
            diagnosticContext,
            LoggingContract.Referer,
            RefererHeader.Sanitise(
                httpContext.Request.Headers[HeaderNames.Referer].ToString(),
                options.MaximumRefererLength));

        Set(
            diagnosticContext,
            LoggingContract.UserAgent,
            HeaderValue.Truncate(
                httpContext.Request.Headers[HeaderNames.UserAgent].ToString(),
                options.MaximumUserAgentLength));

        Set(diagnosticContext, LoggingContract.RequestHost, httpContext.Request.Host.Value);
        Set(diagnosticContext, LoggingContract.RequestScheme, httpContext.Request.Scheme);

        if (httpContext.User?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        // Absent rather than null, the same rule the log context follows. A claim can be
        // missing from an old cookie, and an empty property is harder to filter on than one
        // that is not there. An authenticated principal reaching here with no id claim, or
        // with one holding an empty string, therefore still logs no UserId: the resolvers
        // treat blank as absent so both call sites give one answer.
        Set(diagnosticContext, LoggingContract.UserId, options.ResolveUserId(httpContext.User));
        Set(diagnosticContext, LoggingContract.UserEmail, options.ResolveUserEmail(httpContext.User));
    }

    /// <summary>
    /// Sets a property only when there is a value, so an absent header stays absent rather
    /// than becoming an empty string nobody can filter on. Whitespace counts as absent, which
    /// is the same rule <c>HeaderValue.Truncate</c> and the user resolvers follow.
    /// </summary>
    private static void Set(IDiagnosticContext diagnosticContext, string propertyName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            diagnosticContext.Set(propertyName, value);
        }
    }
}
