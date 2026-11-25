using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Ported with the engine. The allowlist is passed in, which is the one thing the port
/// turned into a parameter.
/// </summary>
public class RequestQueryTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "handler", "batchId", "replacesOrderId", "replacesReference", "copyFrom",
        "pageNumber", "listPage", "route", "from", "to", "sort", "desc"
    };

    private static IQueryCollection Query(string queryString) =>
        new QueryFeature(new FeatureCollection { [typeof(IHttpRequestFeature)] = new HttpRequestFeature { QueryString = queryString } }).Query;

    [Theory]
    [InlineData("?route=Express&replacesOrderId=0f8fad5b-d9cb-469f-a165-70867728950e",
        "replacesOrderId=0f8fad5b-d9cb-469f-a165-70867728950e&route=Express")]
    [InlineData("?batchId=0f8fad5b-d9cb-469f-a165-70867728950e", "batchId=0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("?handler=Csv&PageNumber=2", "handler=Csv&PageNumber=2")]
    [InlineData("?replacesReference=ORD-000000012", "replacesReference=ORD-000000012")]
    [InlineData("?from=2026-11-01&to=2026-11-30", "from=2026-11-01&to=2026-11-30")]
    // Free text, a return URL, an email and a token: the key and that it held something, never what.
    [InlineData("?search=welcome%20card&listPage=2", "listPage=2&search=(set)")]
    [InlineData("?SearchReference=ORD-000412", "SearchReference=(set)")]
    [InlineData("?returnUrl=%2Faccount%2Forders%3Fid%3Dx", "returnUrl=(set)")]
    [InlineData("?token=abc123", "token=(set)")]
    // A search box: typed in, sent empty, and sent with no equals sign at all.
    [InlineData("?q=Jane%20Doe&sort=value", "q=(set)&sort=value")]
    [InlineData("?q=&desc=true", "desc=true&q=")]
    [InlineData("?q", "q=")]
    // A listed key with a value that is not an id or an option name is withheld the same way.
    [InlineData("?route=someone%40example.com", "route=(set)")]
    [InlineData("?route=", "route=")]
    [InlineData("?copyFrom=0f8fad5b-d9cb-469f-a165-70867728950e&handler=Copy", "copyFrom=0f8fad5b-d9cb-469f-a165-70867728950e&handler=Copy")]
    // A key is caller text as well.
    [InlineData("?someone%40example.com=1", "(other)=(set)")]
    // A repeated key keeps each value once.
    [InlineData("?sort=a&sort=b&sort=a", "sort=a&sort=b")]
    public void The_query_is_cut_to_the_allowlist(string queryString, string expected)
    {
        Assert.Equal(expected, RequestQuery.Describe(Query(queryString), Allowed));
    }

    [Fact]
    public void No_query_writes_no_property_and_a_long_one_is_capped()
    {
        Assert.Null(RequestQuery.Describe(QueryCollection.Empty, Allowed));

        var many = string.Join('&', Enumerable.Range(1, 20).Select(index => $"k{index:00}=1"));
        var described = RequestQuery.Describe(Query("?" + many), Allowed)!;

        Assert.Equal(13, described.Split('&').Length);
        Assert.EndsWith("&…", described, StringComparison.Ordinal);
        Assert.StartsWith("k01=(set)&k02=(set)", described, StringComparison.Ordinal);
    }

    [Fact]
    public void A_query_string_password_or_token_is_logged_by_name_alone()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["password"] = "Typed-This-2026",
            ["token"] = "abcdef123456",
            ["newPassword"] = "Another-One-2026"
        });

        Assert.Equal("newPassword=(set)&password=(set)&token=(set)", RequestQuery.Describe(query, Allowed));
    }

    [Fact]
    public void An_empty_allowlist_withholds_every_value()
    {
        // The default. Switching the property on before listing a key leaks nothing.
        var options = new RequestLogOptions();

        Assert.Empty(options.RequestQueryKeys);
        Assert.Equal("id=(set)&page=(set)&q=", RequestQuery.Describe(Query("?page=2&id=42&q="), options));
    }

    [Fact]
    public void The_options_carry_the_allowlist_the_pair_cap_and_the_value_length()
    {
        var options = new RequestLogOptions { MaximumRequestQueryPairs = 2, MaximumRequestQueryValueLength = 4 };
        options.RequestQueryKeys.Add("ID");
        options.RequestQueryKeys.Add("page");

        // id fits the four characters, page does not, and the third pair is cut.
        Assert.Equal("id=1234&page=(set)&…", RequestQuery.Describe(Query("?id=1234&page=12345&z=1"), options));
    }

    [Fact]
    public void A_value_is_logged_up_to_the_length_and_withheld_past_it()
    {
        var fifty = new string('a', 50);

        Assert.Equal($"route={fifty}", RequestQuery.Describe(Query($"?route={fifty}"), Allowed));
        Assert.Equal("route=(set)", RequestQuery.Describe(Query($"?route={fifty}a"), Allowed));
    }

    [Fact]
    public void A_key_is_named_up_to_forty_characters_and_not_past_it()
    {
        var forty = new string('k', 40);

        Assert.Equal($"{forty}=(set)", RequestQuery.Describe(Query($"?{forty}=1"), Allowed));
        Assert.Equal("(other)=(set)", RequestQuery.Describe(Query($"?{forty}k=1"), Allowed));
    }

    [Theory]
    [InlineData("?route=abc%0A")]
    [InlineData("?route=a%20b")]
    [InlineData("?route=a%2Fb")]
    [InlineData("?route=caf%C3%A9")]
    public void A_listed_key_never_logs_a_line_feed_a_space_a_slash_or_a_non_ascii_letter(string queryString)
    {
        // The line feed is the one case that differs from the engine this was ported from,
        // whose regular expression ended in $ and so accepted a value with a final line feed.
        Assert.Equal("route=(set)", RequestQuery.Describe(Query(queryString), Allowed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_pair_cap_that_keeps_nothing_writes_nothing(int maximumPairs)
    {
        Assert.Null(RequestQuery.Describe(Query("?route=a"), Allowed, maximumPairs));
    }

    [Fact]
    public void A_value_length_that_keeps_nothing_withholds_every_value()
    {
        Assert.Equal("route=(set)", RequestQuery.Describe(Query("?route=a"), Allowed, maximumValueLength: 0));
    }

    [Fact]
    public void A_request_with_no_endpoint_gets_no_query_property()
    {
        // Static files are served before routing, so they never have an endpoint.
        var options = new RequestLogOptions { IncludeRequestQuery = true };
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?v=3");

        var before = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(before, context, options);
        Assert.DoesNotContain(LoggingContract.RequestQuery, before.Properties.Keys);

        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "page"));

        var after = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(after, context, options);
        Assert.Equal("v=(set)", after.String(LoggingContract.RequestQuery));
    }

    [Fact]
    public void An_endpoint_with_no_query_gets_no_query_property()
    {
        // Absent, never blank.
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "page"));

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, new RequestLogOptions { IncludeRequestQuery = true });

        Assert.DoesNotContain(LoggingContract.RequestQuery, recorder.Properties.Keys);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => RequestQuery.Describe(null!, Allowed));
        Assert.Throws<ArgumentNullException>(() => RequestQuery.Describe(QueryCollection.Empty, (ISet<string>)null!));
        Assert.Throws<ArgumentNullException>(() => RequestQuery.Describe(QueryCollection.Empty, (RequestLogOptions)null!));
    }
}
