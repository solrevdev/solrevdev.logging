using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Which of the two refusal filters <see cref="RefusalLoggingExtensions.AddSolrevdevRefusalLogging"/>
/// adds. Both by default.
/// </summary>
public sealed class RefusalLogOptions
{
    /// <summary>
    /// Whether to add <see cref="AntiforgeryRefusalLogFilter"/>. On by default.
    /// </summary>
    public bool LogAntiforgeryRefusals { get; set; } = true;

    /// <summary>
    /// Whether to add <see cref="FormRefusalLogFilter"/>. On by default.
    /// </summary>
    public bool LogFormRefusals { get; set; } = true;
}

/// <summary>
/// Registration for the two refusal filters and the default <see cref="ILoggedRefusal"/>.
/// </summary>
public static class RefusalLoggingExtensions
{
    /// <summary>
    /// Adds <see cref="AntiforgeryRefusalLogFilter"/> and <see cref="FormRefusalLogFilter"/>
    /// as global MVC filters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opt-in. An application that does not call this gets neither filter and logs what it
    /// logged before. Calling it twice adds each filter once.
    /// </para>
    /// <para>
    /// It registers no <see cref="ILoggedRefusal"/>. Register your own adapter, or call
    /// <see cref="AddSolrevdevLoggedRefusal"/>, or leave it out and every form refusal is a
    /// Warning.
    /// </para>
    /// <para>
    /// The user agent on the anti-forgery event is cut to
    /// <see cref="RequestLogOptions.MaximumUserAgentLength"/>, so call
    /// <c>AddSolrevdevRequestLogging</c> as well if that cap is configured; without it the
    /// package default applies.
    /// </para>
    /// </remarks>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <param name="configure">Switches either filter off. Null adds both.</param>
    /// <returns>The <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddSolrevdevRefusalLogging(
        this IServiceCollection services,
        Action<RefusalLogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Read now, not through IOptions: which filters exist is fixed when MVC builds its
        // filter list, and this keeps the choice next to the call that made it.
        var chosen = new RefusalLogOptions();
        configure?.Invoke(chosen);

        services.Configure<MvcOptions>(mvc =>
        {
            if (chosen.LogAntiforgeryRefusals && !Has<AntiforgeryRefusalLogFilter>(mvc))
            {
                mvc.Filters.Add<AntiforgeryRefusalLogFilter>();
            }

            if (chosen.LogFormRefusals && !Has<FormRefusalLogFilter>(mvc))
            {
                mvc.Filters.Add<FormRefusalLogFilter>();
            }
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="LoggedRefusal"/>, one per request, as itself and as
    /// <see cref="ILoggedRefusal"/>.
    /// </summary>
    /// <remarks>
    /// For an application with no refusal state of its own. Inject <see cref="LoggedRefusal"/>
    /// to write and <see cref="ILoggedRefusal"/> to read; both resolve to the same object in
    /// a request. It does nothing to an <see cref="ILoggedRefusal"/> that is already
    /// registered.
    /// </remarks>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/>, for chaining.</returns>
    public static IServiceCollection AddSolrevdevLoggedRefusal(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<LoggedRefusal>();
        services.TryAddScoped<ILoggedRefusal>(provider => provider.GetRequiredService<LoggedRefusal>());

        return services;
    }

    private static bool Has<TFilter>(MvcOptions mvc) =>
        mvc.Filters.OfType<TypeFilterAttribute>().Any(filter => filter.ImplementationType == typeof(TFilter));
}
