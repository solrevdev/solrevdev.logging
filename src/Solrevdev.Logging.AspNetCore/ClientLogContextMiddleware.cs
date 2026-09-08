using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Puts the request's trace id and the caller's address onto every log event raised while
/// handling it, signed in or not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="UserLogContextMiddleware"/> because of where each one has
/// to sit, and the split was measured rather than reasoned.</strong> The user one can only
/// run between <c>UseAuthentication</c> and <c>UseAuthorization</c>, which is BELOW the rate
/// limiter, and the limiter short-circuits a refused authentication POST before either is
/// reached. Before the split, tripping a login rate limiter against a running site produced
/// 429 responses with no address and no trace id on anything logged while refusing the
/// caller. That is the single event on a site somebody is most likely to go looking for.
/// </para>
/// <para>
/// So this runs at the TOP of the pipeline, immediately after <c>UseForwardedHeaders</c>,
/// which is what makes the address trustworthy: the framework has either accepted an
/// <c>X-Forwarded-For</c> entry from a peer inside <c>ForwardedHeaders:KnownNetworks</c> or
/// left the peer's own address alone. This middleware never reads a forwarding header itself;
/// doing so would let callers choose the address they are logged under.
/// </para>
/// <para>
/// Everything between the two positions can short-circuit: the rate limiter refuses
/// authentication POSTs, <c>UseHttpsRedirection</c> answers 307, <c>UseStaticFiles</c> serves
/// and returns, and the exception handler runs above all of them.
/// </para>
/// <para>
/// The address is rendered through <see cref="ClientAddress.Describe"/> because a log property
/// is read by a person. A rate limiter's partition key and any stored column must NOT be, and
/// that is deliberate: for every routable address the strings are identical, and they differ
/// only in how loopback and an IPv4-mapped address are spelled.
/// </para>
/// </remarks>
/// <param name="next">The next <see cref="RequestDelegate"/> in the pipeline.</param>
public sealed class ClientLogContextMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>
    /// Pushes the trace id and the caller's address for the rest of the request.
    /// </summary>
    /// <param name="context">The <see cref="HttpContext"/>.</param>
    /// <returns>A task that completes when the rest of the pipeline has run.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Correlation is not identity. An anonymous request is exactly the one somebody chases
        // later: a password reset that never arrived is queued with no user at all.
        var traceId = Activity.Current?.TraceId.ToString();

        // Absent rather than null when there is no address, which in practice is a test host
        // and nothing else. An absent property is filterable and a null one is not.
        var clientIp = ClientAddress.Describe(context.Connection?.RemoteIpAddress);

        using var traceScope = traceId is null
            ? null
            : LogContext.PushProperty(LoggingContract.TraceId, traceId);
        using var clientIpScope = clientIp is null
            ? null
            : LogContext.PushProperty(LoggingContract.ClientIp, clientIp);

        await _next(context).ConfigureAwait(false);
    }
}
