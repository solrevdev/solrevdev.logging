using Microsoft.AspNetCore.Builder;
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

        return app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = RequestSummaryEnricher.EnrichRequestSummary;
            options.GetLevel = (httpContext, elapsed, exception) =>
                RequestLogLevel.For(httpContext, exception);

            configure?.Invoke(options);
        });
    }
}
