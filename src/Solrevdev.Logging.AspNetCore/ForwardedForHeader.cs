using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Reassembles the <c>X-Forwarded-For</c> header the caller actually sent.
/// </summary>
/// <remarks>
/// Kept beside <see cref="LoggingContract.ClientIp"/>, which is what the framework SETTLED
/// ON. Recording both is the point: they agree on a normal request through a load balancer,
/// and a mismatch means either somebody forged a header or <c>KnownNetworks</c> no longer
/// matches the subnets the balancer runs in. With only one of the two recorded, neither case
/// is visible at all.
/// </remarks>
public static class ForwardedForHeader
{
    /// <summary>
    /// The default cap on the reassembled claim, in characters. Shorter than the other
    /// headers because a legitimate value is a short list of addresses and anything longer is
    /// somebody filling the log.
    /// </summary>
    public const int DefaultMaximumLength = 256;

    /// <summary>
    /// Reassembles the header as the caller sent it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading the header alone is WRONG here, and measured to be wrong rather than assumed.
    /// <c>UseForwardedHeaders</c> CONSUMES the entries it accepts: sending one entry made the
    /// middleware put it on the connection and DELETE the now-empty header, so the property
    /// was absent altogether. One entry is exactly what a single load balancer sends, so the
    /// case that recorded nothing is the case production is in. Three entries in left
    /// <c>"1.2.3.4,5.6.7.8"</c>, which is the LEFTOVER and not the claim.
    /// </para>
    /// <para>
    /// <c>X-Original-For</c> is not the missing half either, and that is the second measured
    /// trap. It holds the peer the middleware REPLACED, with its port on it: a real request
    /// through such a pipeline set it to <c>"[::1]:49725"</c>. It says who connected, not what
    /// was claimed. Its only use here is as a flag that the middleware acted at all.
    /// </para>
    /// <para>
    /// So the consumed entry is recovered from where the middleware actually put it, the
    /// connection, which is <paramref name="settledClientIp"/>. Remaining header plus that is
    /// the header as sent. <c>ForwardLimit</c> is documented as a cap rather than a starting
    /// point, so with the usual limit of 1 exactly one entry is ever consumed; raise it and
    /// only the last entry consumed is recoverable, which is one more reason not to.
    /// </para>
    /// <para>
    /// Deliberately NOT put through <see cref="ClientAddress.Describe"/>. This property is the
    /// claim rather than the settled address, and a claim rewritten before it is recorded is
    /// not evidence of anything. Pass the raw <c>RemoteIpAddress.ToString()</c>.
    /// </para>
    /// </remarks>
    /// <param name="headers">The request headers, read AFTER <c>UseForwardedHeaders</c> has run.</param>
    /// <param name="settledClientIp">The address the middleware settled on, unreshaped.</param>
    /// <param name="maximumLength">The most characters to keep.</param>
    /// <returns>The header as the caller sent it, capped, or null when none was sent.</returns>
    public static string? Claimed(
        IHeaderDictionary headers,
        string? settledClientIp,
        int maximumLength = DefaultMaximumLength)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var remaining = headers[ForwardedHeadersDefaults.XForwardedForHeaderName].ToString();

        // Present only when the middleware trusted the peer and rewrote the connection. An
        // untrusted peer keeps its header intact, and then the remainder IS the whole claim.
        var wasConsumed = headers.ContainsKey(ForwardedHeadersDefaults.XOriginalForHeaderName)
            && !string.IsNullOrEmpty(settledClientIp);

        var claimed = (remaining.Length, wasConsumed) switch
        {
            (0, false) => string.Empty,
            (0, true) => settledClientIp!,
            (_, false) => remaining,
            (_, true) => string.Concat(remaining, ",", settledClientIp)
        };

        return claimed.Length == 0 ? null : HeaderValue.Truncate(claimed, maximumLength);
    }
}
