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
/// The summary is written by <c>UseSerilogRequestLogging</c>, which sits near the OUTSIDE of
/// the pipeline, so by the time it writes, the <c>LogContext</c> scopes pushed by
/// <see cref="ClientLogContextMiddleware"/> and <see cref="UserLogContextMiddleware"/> further
/// in have already been disposed. The diagnostic context is the seam Serilog provides for
/// exactly this, and it reads the principal that the authentication middleware settled
/// further in.
/// </para>
/// <para>
/// That disposal is why nothing here may rely on <c>LogContext</c>. A property pushed by
/// inner middleware and only ever read on the summary event would never land, and would fail
/// silently: the query returns nothing and looks like quiet traffic.
/// </para>
/// </remarks>
public static class RequestSummaryEnricher
{
    private static readonly RequestLogOptions Fallback = new();

    /// <summary>
    /// The delegate handed to <c>options.EnrichDiagnosticContext</c>.
    /// </summary>
    /// <remarks>
    /// Options are read from the request services, so an application that called
    /// <c>AddSolrevdevRequestLogging</c> gets its own configuration and one that did not gets
    /// the defaults. Prefer
    /// <see cref="SerilogRequestLoggingExtensions.UseSolrevdevRequestLogging"/>, which wires
    /// this for you.
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

        // What the caller CLAIMED, kept next to what the framework SETTLED ON above. A value
        // here that does not match ClientIp is the thing worth looking at: either
        // KnownNetworks does not cover the balancer, or somebody wrote an extra entry to name
        // themselves.
        //
        // Deliberately NOT through ClientAddress. The consumed entry is put back as the caller
        // wrote it, because this property is the claim rather than the settled address, and a
        // claim rewritten before it is recorded is not evidence of anything.
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
        // that is not there.
        Set(diagnosticContext, LoggingContract.UserId, options.ResolveUserId(httpContext.User));
        Set(diagnosticContext, LoggingContract.UserEmail, options.ResolveUserEmail(httpContext.User));
    }

    /// <summary>
    /// Sets a property only when there is a value, so an absent header stays absent rather
    /// than becoming an empty string nobody can filter on.
    /// </summary>
    private static void Set(IDiagnosticContext diagnosticContext, string propertyName, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            diagnosticContext.Set(propertyName, value);
        }
    }
}
