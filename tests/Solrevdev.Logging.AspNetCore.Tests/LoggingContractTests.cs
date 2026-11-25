using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Pins the contract to the code. Without these the two drift apart and the drift is silent:
/// a consuming application's own contract test keeps passing against a constant nobody sets.
/// </summary>
public class LoggingContractTests
{
    private static readonly string[] RequiredNames =
    [
        "Application", "Environment", "MachineName", "ProcessId", "ThreadId", "TraceId",
        "RequestMethod", "RequestPath", "RequestHost", "RequestScheme", "StatusCode",
        "Elapsed", "ClientIp", "ForwardedFor", "UserAgent", "Referer"
    ];

    private static readonly string[] AuthenticatedNames = ["UserId", "UserEmail"];

    private static readonly string[] OptionalNames =
    [
        "ClientIp", "TraceId", "ForwardedFor", "Referer", "UserAgent", "UserId", "UserEmail"
    ];

    [Fact]
    public void The_required_set_is_exactly_the_sixteen_names()
    {
        Assert.Equal(RequiredNames.Order(), LoggingContract.RequiredProperties.Order());
    }

    [Fact]
    public void The_authenticated_set_is_exactly_the_two_names()
    {
        Assert.Equal(AuthenticatedNames.Order(), LoggingContract.AuthenticatedProperties.Order());
    }

    [Fact]
    public void The_required_set_is_the_enricher_and_host_sets_together()
    {
        Assert.Equal(
            LoggingContract.EnricherProperties.Concat(LoggingContract.HostProperties).Order(),
            LoggingContract.RequiredProperties.Order());
    }

    [Fact]
    public void The_two_ownership_sets_do_not_overlap()
    {
        Assert.Empty(LoggingContract.EnricherProperties.Intersect(LoggingContract.HostProperties));
    }

    [Fact]
    public void Everything_together_is_eighteen_properties()
    {
        Assert.Equal(18, LoggingContract.AllProperties.Count);
    }

    [Fact]
    public void The_enricher_sets_exactly_what_it_owns_and_nothing_else()
    {
        // THE DRIFT TEST. A property added to the enricher and not to the contract, or the
        // other way round, fails here rather than in production six weeks later.
        using var activity = new Activity("request").Start();

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, FullyPopulatedRequest(authenticated: false), new RequestLogOptions());

        Assert.Equal(LoggingContract.EnricherProperties.Order(), recorder.Properties.Keys.Order());
    }

    [Fact]
    public void An_authenticated_summary_adds_exactly_the_two_user_properties()
    {
        using var activity = new Activity("request").Start();

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, FullyPopulatedRequest(authenticated: true), new RequestLogOptions());

        Assert.Equal(
            LoggingContract.EnricherProperties.Concat(LoggingContract.AuthenticatedProperties).Order(),
            recorder.Properties.Keys.Order());
    }

    [Fact]
    public void The_optional_set_is_exactly_the_seven_names()
    {
        Assert.Equal(OptionalNames.Order(), LoggingContract.OptionalProperties.Order());
    }

    [Fact]
    public void Every_optional_name_is_one_this_package_owns()
    {
        // The set is about what a request can lack, so a name in it that nothing sets would be
        // a permanent hole in a consumer's contract test rather than an allowance.
        Assert.All(
            LoggingContract.OptionalProperties,
            name => Assert.Contains(name, LoggingContract.AllProperties));

        Assert.Empty(LoggingContract.OptionalProperties.Intersect(LoggingContract.HostProperties));
    }

    [Fact]
    public void The_unconditional_part_of_the_contract_is_what_is_left()
    {
        // The assertion a consuming application should actually make. RequiredProperties names
        // the contract; this is the part of it a request cannot be missing for an ordinary
        // reason.
        Assert.Equal(
            new[] { "Application", "Environment", "MachineName", "ProcessId", "ThreadId",
                    "RequestMethod", "RequestPath", "RequestHost", "RequestScheme",
                    "StatusCode", "Elapsed" }.Order(),
            LoggingContract.RequiredProperties.Except(LoggingContract.OptionalProperties).Order());
    }

    [Fact]
    public void The_opt_in_set_is_exactly_the_three_names()
    {
        Assert.Equal(
            new[] { "Navigation", "RequestQuery", "SpanId" }.Order(),
            LoggingContract.OptInProperties.Order());
    }

    [Fact]
    public void The_extended_set_is_the_opt_in_names_and_event()
    {
        Assert.Equal(
            new[] { "Event", "Navigation", "RequestQuery", "SpanId" }.Order(),
            LoggingContract.ExtendedProperties.Order());
        Assert.Equal("Event", LoggingContract.Event);
    }

    [Fact]
    public void No_extended_name_is_in_a_set_a_consumer_already_asserts()
    {
        // The rule the new set exists for. Consumers assert the older sets on every request,
        // so a name added to one of them fails their tests on upgrade.
        Assert.Empty(LoggingContract.ExtendedProperties.Intersect(LoggingContract.AllProperties));
        Assert.Empty(LoggingContract.ExtendedProperties.Intersect(LoggingContract.RequiredProperties));
        Assert.Empty(LoggingContract.ExtendedProperties.Intersect(LoggingContract.OptionalProperties));
    }

    public static TheoryData<bool, bool, bool> OptInModes => new()
    {
        { false, false, false },
        { true, false, false },
        { false, true, false },
        { false, false, true },
        { true, true, true }
    };

    [Theory]
    [MemberData(nameof(OptInModes))]
    public void The_enricher_sets_exactly_what_the_sets_say_in_each_mode(
        bool navigation,
        bool requestQuery,
        bool spanId)
    {
        // THE DRIFT TEST again, for the opt-in names. With every option off this is the 1.2.0
        // output exactly; each option adds its one name and nothing else.
        using var activity = new Activity("request").Start();

        var context = FullyPopulatedRequest(authenticated: true);
        context.Request.QueryString = new QueryString("?page=2");
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        context.Request.Headers["Sec-Fetch-Site"] = "none";
        context.Request.Headers["Sec-Fetch-Dest"] = "document";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "page"));

        var options = new RequestLogOptions
        {
            IncludeNavigation = navigation,
            IncludeRequestQuery = requestQuery,
            IncludeSpanId = spanId
        };

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, options);

        var expected = LoggingContract.EnricherProperties
            .Concat(LoggingContract.AuthenticatedProperties)
            .Concat(navigation ? [LoggingContract.Navigation] : [])
            .Concat(requestQuery ? [LoggingContract.RequestQuery] : [])
            .Concat(spanId ? [LoggingContract.SpanId] : []);

        Assert.Equal(expected.Order(), recorder.Properties.Keys.Order());

        if (navigation && requestQuery && spanId)
        {
            Assert.Equal(
                LoggingContract.AllProperties.Except(LoggingContract.HostProperties)
                    .Concat(LoggingContract.OptInProperties).Order(),
                recorder.Properties.Keys.Order());
        }
    }

    private static HttpContext FullyPopulatedRequest(bool authenticated)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("crm.example.com");
        context.Request.Method = "GET";
        context.Request.Path = "/admin/orders";
        context.Request.Headers[HeaderNames.UserAgent] = "Mozilla/5.0";
        context.Request.Headers[HeaderNames.Referer] = "https://crm.example.com/admin";
        context.Request.Headers["X-Original-For"] = "[::1]:49725";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.99");

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "42"),
                    new Claim(ClaimTypes.Email, "someone@example.com")
                ],
                "TestAuth"));
        }

        return context;
    }
}
