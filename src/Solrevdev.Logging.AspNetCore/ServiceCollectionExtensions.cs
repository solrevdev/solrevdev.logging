using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Registration for the request logging options.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the request logging options with their defaults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This registers the options, their validation and a marker service, and nothing else. It
    /// adds no rate limiting, no throttling and no <c>ForwardedHeadersOptions</c> policy; which
    /// peers to trust is the consuming application's decision and this package must never make
    /// it. It does not configure Serilog either: the application owns its logger, its sinks and
    /// its enrichers, and the context middleware need <c>Enrich.FromLogContext()</c> on that
    /// logger to reach ordinary application events.
    /// </para>
    /// <para>
    /// Calling this is required. <c>UseSolrevdevClientLogContext</c>,
    /// <c>UseSolrevdevUserLogContext</c> and <c>UseSolrevdevRequestLogging</c> each throw
    /// <see cref="InvalidOperationException"/> at startup without it. That guard is what the
    /// marker is for. Before it, an application that forgot this call started anyway and ran on
    /// package defaults it never chose, because <c>IOptions&lt;RequestLogOptions&gt;</c>
    /// resolves to a default instance whether or not this method was called.
    /// </para>
    /// </remarks>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddSolrevdevRequestLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Validated(services);

        return services;
    }

    /// <summary>
    /// Adds the request logging options and configures them.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <param name="configure">Called to configure the options.</param>
    /// <returns>The <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddSolrevdevRequestLogging(
        this IServiceCollection services,
        Action<RequestLogOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        Validated(services).Configure(configure);

        return services;
    }

    /// <summary>
    /// Registers the options with the header caps checked.
    /// </summary>
    /// <remarks>
    /// The caps are an application's own configuration, never a caller's, so the useful place
    /// to refuse a bad one is startup. <c>ValidateOnStart</c> is what moves the failure there
    /// from the first request that happens to carry a user agent, which is where a negative
    /// cap used to surface: as an <c>ArgumentOutOfRangeException</c> thrown out of a log
    /// enricher, on one request, under whichever page the deployment was smoke-tested with.
    /// A cap of zero is refused for the same reason even though nothing throws on it: it means
    /// the property silently never appears, which reads exactly like the enricher being
    /// broken.
    /// </remarks>
    private static OptionsBuilder<RequestLogOptions> Validated(IServiceCollection services)
    {
        // The marker is how the pipeline extensions tell "registered" from "not registered".
        // IOptions cannot: every host calls AddOptions, so IOptions<RequestLogOptions>
        // resolves to a default-constructed instance either way. Measured against a running
        // application, which started and logged with package defaults and never said so.
        services.AddSingleton<RequestLoggingMarkerService>();

        return services.AddOptions<RequestLogOptions>()
            .Validate(
                options => options.MaximumUserAgentLength > 0,
                $"{nameof(RequestLogOptions)}.{nameof(RequestLogOptions.MaximumUserAgentLength)} must be at least 1.")
            .Validate(
                options => options.MaximumRefererLength > 0,
                $"{nameof(RequestLogOptions)}.{nameof(RequestLogOptions.MaximumRefererLength)} must be at least 1.")
            .Validate(
                options => options.MaximumForwardedForLength > 0,
                $"{nameof(RequestLogOptions)}.{nameof(RequestLogOptions.MaximumForwardedForLength)} must be at least 1.")
            .ValidateOnStart();
    }
}
