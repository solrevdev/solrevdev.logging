using Microsoft.AspNetCore.Http;

namespace Solrevdev.Logging.AspNetCore.Tests;

public class ForwardedForHeaderTests
{
    private static IHeaderDictionary Headers(params (string Name, string Value)[] headers)
    {
        var dictionary = new HeaderDictionary();

        foreach (var (name, value) in headers)
        {
            dictionary[name] = value;
        }

        return dictionary;
    }

    [Fact]
    public void No_header_and_no_middleware_action_sets_nothing()
    {
        Assert.Null(ForwardedForHeader.Claimed(Headers(), "203.0.113.99"));
    }

    [Fact]
    public void The_consumed_single_entry_is_recovered_from_the_connection()
    {
        // The production case. One entry in, UseForwardedHeaders takes it, puts it on the
        // connection and deletes the now-empty header, leaving X-Original-For behind as the
        // only evidence it acted.
        var headers = Headers(("X-Original-For", "[::1]:49725"));

        Assert.Equal("203.0.113.99", ForwardedForHeader.Claimed(headers, "203.0.113.99"));
    }

    [Fact]
    public void A_leftover_header_is_recombined_with_the_settled_address()
    {
        var headers = Headers(
            ("X-Forwarded-For", "203.0.113.66,203.0.113.11"),
            ("X-Original-For", "[::1]:49725"));

        Assert.Equal(
            "203.0.113.66,203.0.113.11,203.0.113.99",
            ForwardedForHeader.Claimed(headers, "203.0.113.99"));
    }

    [Fact]
    public void An_untrusted_peer_keeps_its_header_whole()
    {
        // No X-Original-For means the middleware did not act, so the remaining header IS the
        // whole claim and nothing is appended to it.
        var headers = Headers(("X-Forwarded-For", "203.0.113.99"));

        Assert.Equal("203.0.113.99", ForwardedForHeader.Claimed(headers, "203.0.113.10"));
    }

    [Fact]
    public void X_original_for_without_a_settled_address_is_not_treated_as_consumption()
    {
        var headers = Headers(("X-Original-For", "[::1]:49725"));

        Assert.Null(ForwardedForHeader.Claimed(headers, null));
    }

    [Fact]
    public void The_claim_is_capped()
    {
        var headers = Headers(("X-Forwarded-For", new string('9', 400)));

        Assert.Equal(
            ForwardedForHeader.DefaultMaximumLength,
            ForwardedForHeader.Claimed(headers, "203.0.113.99")!.Length);
    }

    [Fact]
    public void Null_headers_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ForwardedForHeader.Claimed(null!, "203.0.113.99"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_cap_that_keeps_nothing_returns_nothing_rather_than_throwing(int maximumLength)
    {
        // A misconfigured cap used to reach a range slice and throw out of a log enricher, on
        // a request, in production. Keeping zero characters and keeping no property are the
        // same outcome, so the helper omits; the configuration mistake is refused at startup
        // by AddSolrevdevRequestLogging instead, which is where it can be read.
        var headers = Headers(("X-Forwarded-For", "203.0.113.42"));

        Assert.Null(ForwardedForHeader.Claimed(headers, "203.0.113.99", maximumLength));
    }
}
