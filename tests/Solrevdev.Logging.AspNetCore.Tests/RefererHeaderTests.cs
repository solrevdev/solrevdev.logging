namespace Solrevdev.Logging.AspNetCore.Tests;

public class RefererHeaderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_worth_keeping_is_null(string? referer)
    {
        Assert.Null(RefererHeader.Sanitise(referer));
    }

    [Fact]
    public void An_http_referer_keeps_scheme_host_and_path()
    {
        Assert.Equal(
            "https://crm.example.com/admin/orders/edit",
            RefererHeader.Sanitise("https://crm.example.com/admin/orders/edit?tab=lines#top"));
    }

    [Fact]
    public void A_reset_token_in_the_query_never_survives()
    {
        Assert.Equal(
            "https://crm.example.com/auth/resetpassword",
            RefererHeader.Sanitise("https://crm.example.com/auth/resetpassword?id=live-token-value"));
    }

    [Fact]
    public void A_bare_path_does_not_escape_its_query_into_a_file_uri()
    {
        // THE TRAP. Uri.TryCreate accepts a bare path as an absolute file:// URI on a Unix
        // host, and rebuilding from its parts yields
        // "file:///auth/resetpassword%3Fid=live-token-value" with the token escaped INTO the
        // path rather than removed. This is a security control, so it is asserted twice: the
        // exact expected value, and that the token appears nowhere at all.
        var sanitised = RefererHeader.Sanitise("/auth/resetpassword?id=live-token-value");

        Assert.Equal("/auth/resetpassword", sanitised);
        Assert.DoesNotContain("live-token-value", sanitised, StringComparison.Ordinal);
        Assert.DoesNotContain("file:", sanitised!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_protocol_relative_referer_is_cut_by_hand_too()
    {
        var sanitised = RefererHeader.Sanitise("//host.example.com/x?id=live-token-value");

        Assert.Equal("//host.example.com/x", sanitised);
        Assert.DoesNotContain("live-token-value", sanitised, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragment_is_dropped_from_an_unparseable_value()
    {
        Assert.Equal("not a url", RefererHeader.Sanitise("not a url#fragment"));
    }

    [Fact]
    public void A_non_http_scheme_does_not_take_the_uri_route()
    {
        var sanitised = RefererHeader.Sanitise("javascript:alert(1)?id=live-token-value");

        Assert.DoesNotContain("live-token-value", sanitised!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_value_is_capped()
    {
        var referer = "https://crm.example.com/" + new string('a', 1000);

        Assert.Equal(RefererHeader.DefaultMaximumLength, RefererHeader.Sanitise(referer)!.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_cap_that_keeps_nothing_returns_nothing_rather_than_throwing(int maximumLength)
    {
        // "Never throws" has to hold for the cap as well as for the header. A negative cap
        // reached trimmed[..maximumLength] and threw ArgumentOutOfRangeException mid-request.
        Assert.Null(RefererHeader.Sanitise("https://crm.example.com/admin", maximumLength));
    }
}
