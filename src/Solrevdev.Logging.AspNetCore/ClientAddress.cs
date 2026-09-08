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
/// A genuine public IPv6 address comes back untouched. It has no dotted form to convert to,
/// and <see cref="IPAddress.MapToIPv4"/> on one throws rather than returning something wrong.
/// </para>
/// <para>
/// <strong>This is for log properties and nothing else.</strong> Never key a rate limit, a
/// throttle, a cache partition, an authorisation decision or a persisted column off this
/// value. Those need an address that is stable and unique per caller; this one is reshaped to
/// be readable, and reshaping partitions to make a log line prettier is not a trade worth
/// making. An office typically leaves through one NAT address, so any per-address
/// partitioning is load bearing and is changed on purpose or not at all.
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
    /// null when there was no address.
    /// </returns>
    public static string? Describe(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) ? IPAddress.Loopback.ToString() : address.ToString();
    }
}
