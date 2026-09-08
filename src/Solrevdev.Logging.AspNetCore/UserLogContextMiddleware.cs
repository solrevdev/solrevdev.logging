using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Puts the signed-in user onto every log event raised while handling a request.
/// </summary>
/// <remarks>
/// <para>
/// Pushing onto Serilog's <c>LogContext</c> means no call site has to change: every log line
/// already written inside a request gains the two properties. They are properties on the
/// event rather than text in the message, so every existing message template still reads
/// exactly as it did.
/// </para>
/// <para>
/// <strong>The trace id and the caller's address are NOT here, and the split is deliberate.</strong>
/// They belong to every request whether or not anybody is signed in, and this middleware sits
/// below the rate limiter, which short-circuits a refused authentication POST before it is
/// reached. <see cref="ClientLogContextMiddleware"/> pushes them at the top of the pipeline
/// instead.
/// </para>
/// <para>
/// The email address sits alongside the id deliberately. It is personal data on every line for
/// as long as the log store keeps it, and the argument for it is that it answers "who" with no
/// lookup for the people reading these logs, who are usually the same staff the admin pages
/// already show every record to. An application that disagrees can return null from
/// <see cref="RequestLogOptions.UserEmailSelector"/>.
/// </para>
/// </remarks>
/// <param name="next">The next <see cref="RequestDelegate"/> in the pipeline.</param>
/// <param name="options">The request logging options.</param>
public sealed class UserLogContextMiddleware(RequestDelegate next, IOptions<RequestLogOptions> options)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly RequestLogOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <summary>
    /// Pushes the signed-in user onto the log context for the rest of the request.
    /// </summary>
    /// <remarks>
    /// Register this AFTER <c>UseAuthentication</c>, because it reads the principal that
    /// authentication settles, and BEFORE <c>UseAuthorization</c>, so that a refusal is logged
    /// with the name of whoever was refused. That window is the only correct slot and it is
    /// one line wide.
    /// </remarks>
    /// <param name="context">The <see cref="HttpContext"/>.</param>
    /// <returns>A task that completes when the rest of the pipeline has run.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Anonymous requests push NO user rather than pushing nulls. The login page and every
        // static file would otherwise fill the log store with UserId properties that are
        // always empty, and an absent property is easier to filter on than a null one. They
        // still carry the address, the trace id and, on the summary event, the referer and the
        // user agent: returning early for an anonymous caller in the ENRICHER was a bug rather
        // than a saving, and this early return is not the same thing.
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Null here means absent, and the resolvers return null for a claim holding an empty
        // string as well as for one that is missing. That is deliberate: the request summary
        // omits a blank user, so a middleware that pushed UserId="" would have one request
        // answering the same question two ways, and the empty answer is the one nobody can
        // filter on.
        var userId = _options.ResolveUserId(context.User);
        var userEmail = _options.ResolveUserEmail(context.User);

        using var userIdScope = userId is null
            ? null
            : LogContext.PushProperty(LoggingContract.UserId, userId);
        using var userEmailScope = userEmail is null
            ? null
            : LogContext.PushProperty(LoggingContract.UserEmail, userEmail);

        await _next(context).ConfigureAwait(false);
    }
}
