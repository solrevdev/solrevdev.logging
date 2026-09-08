using Microsoft.AspNetCore.Builder;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Registered by <c>AddSolrevdevRequestLogging</c> so the pipeline extensions can tell whether
/// it was called.
/// </summary>
/// <remarks>
/// <para>
/// The marker-service pattern ASP.NET Core uses for routing, MVC and SignalR, and it is here
/// for a measured reason. <c>IOptions&lt;T&gt;</c> cannot answer this question: every host calls
/// <c>AddOptions</c>, so <c>IOptions&lt;RequestLogOptions&gt;</c> resolves to a
/// default-constructed instance whether or not this package was ever registered. Measured
/// against a running application on the published 1.0.0, with no
/// <c>AddSolrevdevRequestLogging</c> call: the options resolved, non-null, carrying
/// <c>/healthcheck,/version</c> and a 512 character cap, and the request was logged with
/// package defaults from end to end. Nothing threw and nothing said so.
/// </para>
/// <para>
/// A private type nobody else can register is the only thing whose presence means "this
/// package was added", which is why it exists rather than a bool or a sentinel option value.
/// </para>
/// </remarks>
internal sealed class RequestLoggingMarkerService
{
    /// <summary>
    /// Throws when <c>AddSolrevdevRequestLogging</c> was not called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scope is exactly one mistake: wiring the middleware without registering the
    /// services. It catches that at startup, where the message can be read, rather than
    /// leaving an application running on defaults it did not choose.
    /// </para>
    /// <para>
    /// It does NOT catch the other half of the same problem. A unit test built on a bare
    /// <c>DefaultHttpContext</c> has no <c>RequestServices</c>, so
    /// <see cref="RequestLogLevel.For(Microsoft.AspNetCore.Http.HttpContext, Exception?)"/> and
    /// <see cref="RequestSummaryEnricher.EnrichRequestSummary"/> still fall back to package
    /// defaults there, silently, and no startup check can see it because no application started.
    /// That half is a documentation and test-wiring matter. Do not describe this guard as
    /// covering it.
    /// </para>
    /// </remarks>
    /// <param name="app">The <see cref="IApplicationBuilder"/> being configured.</param>
    /// <exception cref="InvalidOperationException">The services were not registered.</exception>
    internal static void ThrowIfUnregistered(IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService(typeof(RequestLoggingMarkerService)) is null)
        {
            throw new InvalidOperationException(
                "Unable to find the required services. Please add all the required services by "
                + "calling 'IServiceCollection.AddSolrevdevRequestLogging' in the application "
                + "startup code.");
        }
    }
}
