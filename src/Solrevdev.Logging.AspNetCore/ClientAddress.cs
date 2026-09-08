using System.Net;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Renders a caller's address for a log property, in the form a person reading Seq expects.
/// </summary>
/// <remarks>
/// <para>
/// Kestrel listens on a dual-stack socket, so an IPv4 caller arrives as
/// <c>::ffff:203.0.113.42</c> and a request from the same machine arrives as <c>::1</c>.
/// Both read as a different client from the dotted form the same caller produces elsewhere,
/// so a log filter on either spelling silently returns half the traffic.
/// </para>
/// <para>
/// <strong>The trap is that <c>::1</c> is NOT an IPv4-mapped address.</strong>
/// <see cref="IPAddress.IsIPv4MappedToIPv6"/> is <c>false</c> for it, measured on .NET 10,
/// because mapping means the <c>::ffff:0:0/96</c> prefix and loopback is its own address. The
/// mapped check on its own therefore leaves local traffic reading <c>::1</c>. The explicit
/// <see cref="IPAddress.IsLoopback(IPAddress)"/> test is what catches it.
/// </para>
/// <para>
/// <see cref="IPAddress.IsLoopback(IPAddress)"/> covers the whole of 127.0.0.0/8, so
/// <c>127.0.0.2</c> also renders as <c>127.0.0.1</c>. Two distinct addresses collapse into one
/// string, which is safe here only because nothing counts, buckets or stores the result.
/// </para>
/// <para>
/// <strong>The mapped check runs BEFORE the loopback test, and that order is behaviour rather
/// than layout.</strong> <c>IPAddress.IsLoopback("::ffff:127.0.0.2")</c> is <c>false</c> on the
/// raw value and <c>true</c> once the mapping is dropped, so testing loopback first sends that
/// caller down the mapped branch instead and logs it as <c>127.0.0.2</c> rather than
/// <c>127.0.0.1</c>. Measured on .NET 10 and pinned by a test, because the difference is one
/// digit on one input and every other input passes under either order.
/// </para>
/// <para>
/// <strong>The <see cref="IPAddress.IsIPv4MappedToIPv6"/> guard exists because
/// <see cref="IPAddress.MapToIPv4"/> fails SILENTLY.</strong> Called on a genuine IPv6 address
/// it does not throw and it does not signal anything; it returns the last four bytes as a
/// plausible, routable, completely wrong IPv4 address. Measured on .NET 10:
/// <c>2001:db8:85a3::8a2e:370:7334</c> comes back as <c>3.112.115.52</c>, and both <c>::1</c>
/// and <c>fe80::1</c> come back as <c>0.0.0.1</c>. Without the guard every genuine
/// IPv6 caller would be logged under a fabricated address and nothing would ever raise, so the
/// first sign of it would be an investigation run against an address that never existed.
/// </para>
/// <para>
/// <strong>This is for log properties and nothing else.</strong> Never key a rate limit, a
/// throttle, a cache partition, an authorisation decision or a persisted column off this
/// value. Those need an address that is stable and unique per caller; this one is reshaped to
/// be readable, and reshaping partitions to make a log line prettier is not a trade worth
/// making. An office typically leaves through one NAT address, so any per-address
/// partitioning is load bearing and is changed on purpose or not at all. The authorisation
/// item earns its place in that list: an address-based gate is an ordinary thing to find in
/// an application, whether it admits an office range, unlocks an administrative surface on
/// the local machine or varies what a sign-in page will tell an unauthenticated caller. Any
/// of those, fed this value, is widened by the loopback collapse and moved by the mapped
/// rewrite, and it fails open rather than loudly.
/// </para>
/// <para>
/// For every address that arrives in native, non-mapped, non-loopback form the reshaped and
/// the raw string are identical, which is what lets the mistake survive testing. They differ
/// only for loopback and for a mapped address, and a mapped address is routable: an IPv4
/// caller arriving as <c>::ffff:203.0.113.42</c> is logged here as <c>203.0.113.42</c> and
/// by anything calling <c>ToString()</c> as the mapped spelling.
/// </para>
/// </remarks>
public static class ClientAddress
{
    /// <summary>
    /// Renders an address for a log property.
    /// </summary>
    /// <param name="address">
    /// The address off the connection, read after <c>UseForwardedHeaders</c> has run. Null is
    /// normal rather than exceptional, because a test host never sets one.
    /// </param>
    /// <returns>
    /// The dotted IPv4 form for anything that has one, the address unchanged otherwise, and
    /// <c>null</c> when there was no address. Three parts of that are surprising enough to be
    /// worth stating rather than leaving to be discovered:
    /// <list type="bullet">
    /// <item><description>
    /// ALL of 127.0.0.0/8 collapses to <c>127.0.0.1</c>, not just <c>::1</c>. Both
    /// <c>127.0.0.2</c> and <c>::ffff:127.0.0.2</c> come back as <c>127.0.0.1</c>, so two
    /// distinct callers are indistinguishable in the log.
    /// </description></item>
    /// <item><description>
    /// A link-local address keeps its zone suffix and is rendered verbatim, so
    /// <c>fe80::1%1</c> comes back as <c>fe80::1%1</c>, scope id and all.
    /// </description></item>
    /// <item><description>
    /// A genuine public IPv6 address is returned unchanged, because it has no dotted form to
    /// be converted to.
    /// </description></item>
    /// </list>
    /// </returns>
    public static string? Describe(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        // This order is load bearing, not layout. IsLoopback is false for ::ffff:127.0.0.2 and
        // true once the mapping is dropped, so the mapped check has to run first.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) ? IPAddress.Loopback.ToString() : address.ToString();
    }
}
