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
}
