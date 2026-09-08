using System.Net;

namespace Solrevdev.Logging.AspNetCore.Tests;

public class ClientAddressTests
{
    [Fact]
    public void Null_in_null_out()
    {
        Assert.Null(ClientAddress.Describe(null));
    }

    [Fact]
    public void An_ipv4_mapped_address_loses_its_mapping()
    {
        Assert.Equal("203.0.113.42", ClientAddress.Describe(IPAddress.Parse("::ffff:203.0.113.42")));
    }

    [Fact]
    public void Ipv6_loopback_renders_dotted()
    {
        // The whole reason the loopback test exists. Assert the measured fact first, so a
        // future runtime change that makes ::1 report as mapped fails HERE and not silently.
        Assert.False(IPAddress.Parse("::1").IsIPv4MappedToIPv6);
        Assert.Equal("127.0.0.1", ClientAddress.Describe(IPAddress.Parse("::1")));
    }

    [Fact]
    public void Ipv4_loopback_is_unchanged()
    {
        Assert.Equal("127.0.0.1", ClientAddress.Describe(IPAddress.Parse("127.0.0.1")));
    }

    [Fact]
    public void The_whole_of_the_loopback_range_collapses_to_one_address()
    {
        // IsLoopback covers 127.0.0.0/8, so two distinct addresses become one string. Safe
        // only because nothing counts, buckets or stores this value.
        Assert.Equal("127.0.0.1", ClientAddress.Describe(IPAddress.Parse("127.0.0.2")));
    }

    [Fact]
    public void A_public_ipv6_address_passes_through_untouched()
    {
        const string address = "2001:db8:85a3::8a2e:370:7334";

        Assert.Equal(address, ClientAddress.Describe(IPAddress.Parse(address)));
    }

    [Fact]
    public void A_public_ipv4_address_passes_through_untouched()
    {
        Assert.Equal("203.0.113.99", ClientAddress.Describe(IPAddress.Parse("203.0.113.99")));
    }

    [Fact]
    public void A_mapped_loopback_address_pins_the_order_of_the_two_checks()
    {
        // The highest-value test in this file. It is the ONLY input that discriminates the
        // order of the mapped check and the loopback test: IsLoopback is false on the raw
        // ::ffff:127.0.0.2 and true once the mapping is dropped, so map-then-check gives
        // 127.0.0.1 and check-then-map gives ::ffff:127.0.0.2. Every other case in this file
        // passes under either ordering, so without this test somebody can swap the two lines
        // as a tidy and silently change what a mapped loopback caller is logged under.
        Assert.False(IPAddress.IsLoopback(IPAddress.Parse("::ffff:127.0.0.2")));
        Assert.True(IPAddress.Parse("::ffff:127.0.0.2").IsIPv4MappedToIPv6);

        Assert.Equal("127.0.0.1", ClientAddress.Describe(IPAddress.Parse("::ffff:127.0.0.2")));
    }

    [Fact]
    public void MapToIPv4_on_a_genuine_ipv6_address_truncates_silently()
    {
        // Documents WHY the IsIPv4MappedToIPv6 guard exists, rather than leaving it to be
        // remembered. MapToIPv4 does not throw on a genuine IPv6 address and does not signal
        // anything; it returns the last four bytes as a plausible, routable, completely wrong
        // IPv4 address. Measured on .NET 10. Without the guard every IPv6 caller would be
        // logged under a fabricated address and nothing would ever raise.
        Assert.Equal(
            "3.112.115.52",
            IPAddress.Parse("2001:db8:85a3::8a2e:370:7334").MapToIPv4().ToString());
        Assert.Equal("0.0.0.1", IPAddress.Parse("fe80::1").MapToIPv4().ToString());

        // And the guard holds: the address the helper returns is the real one.
        Assert.Equal(
            "2001:db8:85a3::8a2e:370:7334",
            ClientAddress.Describe(IPAddress.Parse("2001:db8:85a3::8a2e:370:7334")));
    }

    [Fact]
    public void A_link_local_address_keeps_its_zone_suffix()
    {
        // Documented in the <returns>, so pinned here. The scope id survives verbatim, which
        // means two callers on different interfaces stay distinguishable in the log.
        Assert.Equal("fe80::1%1", ClientAddress.Describe(IPAddress.Parse("fe80::1%1")));
    }
}
