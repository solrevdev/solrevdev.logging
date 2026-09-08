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
