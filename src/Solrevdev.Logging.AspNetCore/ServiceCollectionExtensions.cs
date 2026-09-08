using Microsoft.Extensions.DependencyInjection;

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
    /// This registers options and nothing else. It adds no rate limiting, no throttling and
    /// no <c>ForwardedHeadersOptions</c> policy; which peers to trust is the consuming
    /// application's decision and this package must never make it.
    /// </remarks>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddSolrevdevRequestLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<RequestLogOptions>();

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

        services.AddOptions<RequestLogOptions>().Configure(configure);

        return services;
    }
}
