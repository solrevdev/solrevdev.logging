using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;

using SerilogRequestLoggingOptions = Serilog.AspNetCore.RequestLoggingOptions;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Configures <c>UseSerilogRequestLogging</c> with this package's enricher and level function.
/// </summary>
/// <remarks>
/// This package's own options type is <see cref="RequestLogOptions"/> rather than
/// <c>RequestLoggingOptions</c> on purpose: Serilog.AspNetCore already owns that name in the
/// same conceptual space, and two types called the same thing in one file is a using-directive
/// argument nobody should have to have.
/// </remarks>
public static class SerilogRequestLoggingExtensions
{
    /// <summary>
    /// Adds Serilog's request logging, enriched to the <see cref="LoggingContract"/> and with
    /// static files and probes demoted to Verbose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Register it immediately AFTER <see cref="ApplicationBuilderExtensions.UseSolrevdevClientLogContext"/>
    /// and BEFORE <c>UseStaticFiles</c>. Sitting above the static files is what makes
    /// <see cref="RequestLogLevel"/> necessary rather than optional: without the level function
    /// every stylesheet writes an Information event.
    /// </para>
    /// <para>
    /// An application's own summary fields go in
    /// <see cref="RequestLogOptions.SummaryEnrichers"/>, which run after the package's. That
    /// is the seam to use in place of calling <c>UseSerilogRequestLogging</c> directly and
    /// wiring <see cref="RequestSummaryEnricher"/> and <see cref="RequestLogLevel"/> by hand,
    /// which skips the startup guard below.
    /// </para>
    /// <para>
    /// The options used are the ones the application registered, read from its own service
    /// provider. They do not depend on <c>HttpContext.RequestServices</c>.
    /// </para>
    /// <para>
    /// The full required order is on <see cref="ApplicationBuilderExtensions"/>:
    /// <c>UseForwardedHeaders</c>, client log context, this, <c>UseStaticFiles</c>,
    /// <c>UseRouting</c>, the rate limiter, <c>UseAuthentication</c>, user log context,
    /// <c>UseAuthorization</c>.
    /// </para>
    /// </remarks>
    /// <param name="app">The <see cref="IApplicationBuilder"/>.</param>
    /// <param name="configure">
    /// Called after the enricher and level function are set, so an application can add a
    /// message template or further enrichment. Replacing <c>EnrichDiagnosticContext</c> or
    /// <c>GetLevel</c> here undoes the point of the helper.
    /// </param>
    /// <returns>The <see cref="IApplicationBuilder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>AddSolrevdevRequestLogging</c> was not called.
    /// </exception>
    public static IApplicationBuilder UseSolrevdevRequestLogging(
        this IApplicationBuilder app,
        Action<SerilogRequestLoggingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        RequestLoggingMarkerService.ThrowIfUnregistered(app);

        // Read from the application's own services, once, and not from each request's. The
        // marker check above means the registration is there. Before 1.3.0 this handed over
        // the short overloads, which look in HttpContext.RequestServices and fall back to
        // package defaults without a word when it is null. Under a host it never is, so the
        // application's options were always the ones used; this takes the question away.
        var registered = app.ApplicationServices.GetRequiredService<IOptions<RequestLogOptions>>();

        return app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
                RequestSummaryEnricher.Enrich(diagnosticContext, httpContext, registered.Value);
            options.GetLevel = (httpContext, elapsed, exception) =>
                RequestLogLevel.For(httpContext, exception, registered.Value);

            configure?.Invoke(options);
        });
    }
}
