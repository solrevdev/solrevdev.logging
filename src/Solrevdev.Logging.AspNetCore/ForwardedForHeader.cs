using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Rebuilds an approximation of the <c>X-Forwarded-For</c> the caller sent, for diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// Kept beside <see cref="LoggingContract.ClientIp"/>, which is what the framework SETTLED
/// ON. Recording both is the point: they agree on a normal request through a load balancer,
/// and a difference is worth looking at. With only one of the two recorded, neither the
/// forged-entry case nor the misconfigured-trust case is visible at all.
/// </para>
/// <para>
/// <strong>It is a best-effort reconstruction of UNTRUSTED information, not a copy of the
/// header.</strong> The limits are in <see cref="Claimed"/> and they are not edge cases. A
/// difference between this and <see cref="LoggingContract.ClientIp"/> is a reason to
/// investigate, never proof of spoofing: extra proxies, an address with a port on it, IPv6
/// text that the framework re-spelled, and the truncation described below all produce one
/// without anybody forging anything. Never use this value for identity, for access, or for
/// anything but reading.
/// </para>
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
    /// Rebuilds what the caller appears to have claimed, from what the middleware left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading the header alone is WRONG here, and measured to be wrong rather than assumed.
    /// <c>UseForwardedHeaders</c> CONSUMES the entries it accepts: sending one entry made the
    /// middleware put it on the connection and DELETE the now-empty header, so the property
    /// was absent altogether. One entry is exactly what a single load balancer sends, so the
    /// case that recorded nothing is the case production is in. Three entries in left
    /// <c>"203.0.113.66,203.0.113.11"</c>, which is the LEFTOVER and not the claim.
    /// </para>
    /// <para>
    /// <c>X-Original-For</c> is not the missing half either, and that is the second measured
    /// trap. It holds the peer the middleware REPLACED, with its port on it: a real request
    /// through such a pipeline set it to <c>"[::1]:49725"</c>. It says who connected, not what
    /// was claimed.
    /// </para>
    /// <para>
    /// So the consumed entry is recovered from where the middleware actually put it, the
    /// connection, which is <paramref name="settledClientIp"/>. Remaining header plus that is
    /// an approximation of the header as sent.
    /// </para>
    /// <para>
    /// <strong>Four things this cannot do, each measured rather than feared:</strong>
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <c>X-Original-For</c> is a HINT that the middleware acted, not evidence of it. Any
    /// caller can send that header. An untrusted peer at <c>203.0.113.90</c> sending
    /// <c>X-Original-For</c> and no <c>X-Forwarded-For</c> produces
    /// <c>ForwardedFor=203.0.113.90</c> from this method: a forwarding claim where none was
    /// made. <see cref="LoggingContract.ClientIp"/> is unaffected, because it comes off the
    /// connection and never off a header. Nothing in the request distinguishes that case from
    /// a trusted single hop, which is why it is documented rather than fixed.
    /// </description></item>
    /// <item><description>
    /// A port is lost. A trusted <c>X-Forwarded-For: 203.0.113.42:54321</c>, which is what an
    /// AWS load balancer can append, comes back as <c>203.0.113.42</c>, because the port went
    /// onto <c>Connection.RemotePort</c> and the address alone is what is recovered.
    /// </description></item>
    /// <item><description>
    /// Only the LAST consumed entry is recoverable, because the connection holds one address.
    /// With two trusted hops consumed, <c>203.0.113.42,203.0.113.11</c> comes back as
    /// <c>203.0.113.42</c> and the intermediate hop is gone. <c>ForwardLimit</c> is documented
    /// as a cap rather than a starting point, so the usual limit of 1 consumes exactly one
    /// entry and loses nothing here; that is a reason to know your own limit, not a reason for
    /// this library to impose one.
    /// </description></item>
    /// <item><description>
    /// The recovered entry is the framework's spelling of the address, not the caller's
    /// characters. <see cref="System.Net.IPAddress"/> round-trips IPv6 text through its own
    /// normal form, so a caller's <c>2001:0db8:0000:0000:0000:0000:0000:0001</c> comes back as
    /// <c>2001:db8::1</c>. The default header names are assumed as well, so an application
    /// that renamed them through <c>ForwardedHeadersOptions</c> gets the leftover half only.
    /// </description></item>
    /// </list>
    /// <para>
    /// Deliberately NOT put through <see cref="ClientAddress.Describe"/>. This property is the
    /// claim rather than the settled address, and a claim reshaped before it is recorded is
    /// worth even less than one that is merely incomplete. Pass the raw
    /// <c>RemoteIpAddress.ToString()</c>.
    /// </para>
    /// </remarks>
    /// <param name="headers">The request headers, read AFTER <c>UseForwardedHeaders</c> has run.</param>
    /// <param name="settledClientIp">The address the middleware settled on, unreshaped.</param>
    /// <param name="maximumLength">The most characters to keep. Zero or less keeps nothing.</param>
    /// <returns>
    /// A capped, best-effort reconstruction of the forwarding claim, or null when there is
    /// nothing to report.
    /// </returns>
    public static string? Claimed(
        IHeaderDictionary headers,
        string? settledClientIp,
        int maximumLength = DefaultMaximumLength)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var remaining = headers[ForwardedHeadersDefaults.XForwardedForHeaderName].ToString();

        // Set by the middleware when it trusted the peer and rewrote the connection, and also
        // settable by anyone at all, which is why the summary property is a clue rather than
        // evidence. An untrusted peer that sends neither header keeps its own claim intact,
        // and then the remainder IS the whole claim.
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
