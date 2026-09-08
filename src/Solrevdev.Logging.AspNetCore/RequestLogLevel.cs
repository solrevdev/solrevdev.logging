using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Decides what level Serilog's one-line-per-request summary is written at.
/// </summary>
/// <remarks>
/// <para>
/// Request logging is registered ABOVE <c>UseStaticFiles</c>, so without this every
/// stylesheet, script, font and icon writes an Information event into a log store shared with
/// every other application on the box. One page load is a single page event and roughly
/// thirty asset events, and none of the thirty has ever answered a question. The health probe
/// is included for the same reason: a monitor polling every thirty seconds writes 2,880
/// events a day that say nothing.
/// </para>
/// <para>
/// <strong>Verbose rather than dropped, and be clear about what that means.</strong> Nothing
/// sinks Verbose in these applications, so the events are discarded before any sink sees them
/// and are never even formatted. Chasing one back means lowering
/// <c>MinimumLevel:Default</c>, not lowering a sink's level, and it takes no deploy.
/// </para>
/// <para>
/// A quiet path that FAILS keeps its event. A 500 is an Error and a 4xx keeps its Information
/// event, because a missing stylesheet is the one time these paths are worth a line and
/// hiding it is how a half-copied deploy looks like a working one.
/// </para>
/// </remarks>
public static class RequestLogLevel
{
    private static readonly RequestLogOptions Fallback = new();

    /// <summary>
    /// The level for one request's summary event, reading options from the request services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape <c>UseSerilogRequestLogging</c> wants for
    /// <c>options.GetLevel</c> once the elapsed argument is dropped. Prefer
    /// <see cref="SerilogRequestLoggingExtensions.UseSolrevdevRequestLogging"/>, which wires
    /// it for you.
    /// </para>
    /// <para>
    /// <strong>It falls back to the package defaults, silently, when the options cannot be
    /// resolved.</strong> That is either a null <c>HttpContext.RequestServices</c> or an
    /// application that wired the middleware without calling
    /// <c>AddSolrevdevRequestLogging</c>. Nothing is logged about it, because a level function
    /// runs on every request and a warning per request would be worse than the fallback.
    /// </para>
    /// <para>
    /// It matters most in a test. A bare <c>DefaultHttpContext</c> has no
    /// <c>RequestServices</c>, so a test that calls this overload measures the package
    /// defaults while appearing to measure the application's configuration. A configured
    /// <c>/version</c> prefix comes back Verbose there whatever the application set. Either
    /// give the context a <c>RequestServices</c> built from the application's own
    /// registrations, or call
    /// <see cref="For(HttpContext, Exception?, RequestLogOptions)"/> with the options in hand.
    /// </para>
    /// <para>
    /// The other way to reach the fallback, an application that wired the middleware without
    /// calling <c>AddSolrevdevRequestLogging</c>, is refused at startup by
    /// <see cref="RequestLoggingMarkerService"/>. That guard covers the application. It cannot
    /// cover the test above, because no application starts there.
    /// </para>
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="exception">The exception the pipeline surfaced, if one did.</param>
    /// <returns>The level to write the summary at.</returns>
    public static LogEventLevel For(HttpContext context, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.RequestServices?
            .GetService<IOptions<RequestLogOptions>>()?.Value ?? Fallback;

        return For(context, exception, options);
    }

    /// <summary>
    /// The level for one request's summary event, against explicit options.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="exception">The exception the pipeline surfaced, if one did.</param>
    /// <param name="options">The options.</param>
    /// <returns>The level to write the summary at.</returns>
    public static LogEventLevel For(HttpContext context, Exception? exception, RequestLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        // Error first, and unconditionally. A stylesheet that 500s is not routine, and a
        // health probe that fails is the most interesting line in the file.
        if (exception is not null || context.Response.StatusCode >= 500)
        {
            return LogEventLevel.Error;
        }

        if (context.Response.StatusCode < 400 && IsQuiet(context.Request.Path, options))
        {
            return LogEventLevel.Verbose;
        }

        return LogEventLevel.Information;
    }

    /// <summary>
    /// Whether a path is one whose successful requests say nothing worth keeping.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="options">The options carrying the quiet prefixes and extensions.</param>
    /// <returns>True when the path names a static asset or a configured quiet prefix.</returns>
    public static bool IsQuiet(PathString path, RequestLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!path.HasValue)
        {
            return false;
        }

        foreach (var prefix in options.QuietPathPrefixes)
        {
            if (!string.IsNullOrEmpty(prefix)
                && path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var extension = Path.GetExtension(path.Value!);

        return !string.IsNullOrEmpty(extension) && options.QuietFileExtensions.Contains(extension);
    }
}
