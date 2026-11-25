using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Logs a form post that failed its anti-forgery check, with the reason, as
/// <see cref="RefusalEvents.WebAntiforgeryRefused"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ported from an application that had run it in production. The framework's own line for
/// this is Information under <c>Microsoft.AspNetCore</c>, which is commonly floored at
/// Warning, so the person gets a bare 400 and the log gets a request summary saying 400 and
/// nothing else. A sign-in form that fails this way looks, from the log, like a caller who
/// gave up.
/// </para>
/// <para>
/// An always-run result filter because the anti-forgery check is an authorization filter: it
/// short-circuits before any page handler or ordinary result filter runs. The reason is read
/// by validating again, which costs one cookie parse and only on the failure path.
/// </para>
/// <para>
/// The event is a Warning and carries <see cref="LoggingContract.RequestMethod"/>,
/// <see cref="LoggingContract.RequestPath"/>, <see cref="LoggingContract.RefusalReason"/>,
/// <see cref="LoggingContract.HasAntiforgeryCookie"/> and
/// <see cref="LoggingContract.UserAgent"/>. The user agent is cut to
/// <see cref="RequestLogOptions.MaximumUserAgentLength"/>, as the request summary cuts it,
/// and is absent when the caller sent none.
/// </para>
/// <para>
/// Add it with <see cref="RefusalLoggingExtensions.AddSolrevdevRefusalLogging"/>. The event
/// itself is written by <see cref="AntiforgeryRefusalLog"/>, which code outside MVC calls too.
/// <see cref="IAntiforgery"/> is looked up per refusal and not taken in the constructor: a
/// global filter is built for every MVC request, and an application with no anti-forgery
/// services must not fail there.
/// </para>
/// </remarks>
/// <param name="logger">The logger.</param>
/// <param name="options">The request logging options, for the user agent cap.</param>
public sealed class AntiforgeryRefusalLogFilter(
    ILogger<AntiforgeryRefusalLogFilter> logger,
    IOptions<RequestLogOptions> options) : IAsyncAlwaysRunResultFilter
{
    /// <summary>
    /// The <see cref="LoggingContract.RefusalReason"/> when the check, run again, did not say.
    /// The same value as <see cref="AntiforgeryRefusalLog.UnknownReason"/>.
    /// </summary>
    public const string UnknownReason = AntiforgeryRefusalLog.UnknownReason;

    private readonly ILogger<AntiforgeryRefusalLogFilter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly RequestLogOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <summary>
    /// Writes the event when the result is the framework's anti-forgery failure, then lets
    /// the result run.
    /// </summary>
    /// <param name="context">The result about to run.</param>
    /// <param name="next">The rest of the result pipeline.</param>
    /// <returns>A task that completes when the result has run.</returns>
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.Result is IAntiforgeryValidationFailedResult)
        {
            // One implementation of the event, shared with minimal-API callers.
            var failure = await AntiforgeryRefusalLog.ReasonAsync(context.HttpContext).ConfigureAwait(false);

            AntiforgeryRefusalLog.WriteCut(_logger, context.HttpContext, failure, _options.MaximumUserAgentLength);
        }

        await next().ConfigureAwait(false);
    }
}
