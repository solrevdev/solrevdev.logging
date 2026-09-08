using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Solrevdev.Logging.AspNetCore.Tests;

public class RequestSummaryEnricherTests
{
    private static DefaultHttpContext Request(bool authenticated = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("crm.example.com");
        context.Request.Method = "GET";
        context.Request.Path = "/admin/orders";
        context.Request.Headers[HeaderNames.UserAgent] = "Mozilla/5.0";
        context.Request.Headers[HeaderNames.Referer] = "https://crm.example.com/admin?tab=1";
        context.Request.Headers["X-Original-For"] = "[::1]:49725";
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.99");

        if (authenticated)
        {
            context.User = Principal();
        }

        return context;
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(
            claims.Length > 0
                ? claims
                :
                [
                    new Claim(ClaimTypes.NameIdentifier, "0f7c2b7e-1f4a-4a1e-8d3a-2f1d9a0b7c11"),
                    new Claim(ClaimTypes.Email, "someone@example.com")
                ],
            "TestAuth"));

    [Fact]
    public void An_anonymous_request_carries_no_user_properties_at_all()
    {
        // Absent, never null. A null property is not filterable in Seq.
        var recorder = new RecordingDiagnosticContext();

        RequestSummaryEnricher.Enrich(recorder, Request(), new RequestLogOptions());

        Assert.DoesNotContain(LoggingContract.UserId, recorder.Properties.Keys);
        Assert.DoesNotContain(LoggingContract.UserEmail, recorder.Properties.Keys);
    }

    [Fact]
    public void An_anonymous_request_still_carries_its_client_context()
    {
        // Returning early for an anonymous caller was a bug rather than a saving: the login
        // page, a 429 and a probe are the requests most worth an address.
        var recorder = new RecordingDiagnosticContext();

        RequestSummaryEnricher.Enrich(recorder, Request(), new RequestLogOptions());

        Assert.Equal("203.0.113.99", recorder.String(LoggingContract.ClientIp));
        Assert.Equal("Mozilla/5.0", recorder.String(LoggingContract.UserAgent));
        Assert.Equal("crm.example.com", recorder.String(LoggingContract.RequestHost));
        Assert.Equal("https", recorder.String(LoggingContract.RequestScheme));
    }

    [Fact]
    public void An_authenticated_request_carries_the_user()
    {
        var recorder = new RecordingDiagnosticContext();

        RequestSummaryEnricher.Enrich(recorder, Request(authenticated: true), new RequestLogOptions());

        Assert.Equal("0f7c2b7e-1f4a-4a1e-8d3a-2f1d9a0b7c11", recorder.String(LoggingContract.UserId));
        Assert.Equal("someone@example.com", recorder.String(LoggingContract.UserEmail));
    }

    [Fact]
    public void The_email_falls_back_to_the_identity_name()
    {
        // A fourteen-day cookie issued before the Email claim existed still has to resolve.
        var context = Request();
        context.User = Principal(
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Name, "old-cookie@example.com"));

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, new RequestLogOptions());

        Assert.Equal("old-cookie@example.com", recorder.String(LoggingContract.UserEmail));
    }

    [Fact]
    public void The_fallback_can_be_switched_off()
    {
        var context = Request();
        context.User = Principal(
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Name, "old-cookie@example.com"));

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(
            recorder,
            context,
            new RequestLogOptions { FallBackToIdentityNameForEmail = false });

        Assert.DoesNotContain(LoggingContract.UserEmail, recorder.Properties.Keys);
    }

    [Fact]
    public void Claim_extraction_can_be_replaced_with_a_delegate()
    {
        var context = Request();
        context.User = Principal(
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim("staff_email", "delegate@example.com"));

        var options = new RequestLogOptions
        {
            UserIdSelector = principal => "custom-" + principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            UserEmailSelector = principal => principal.FindFirst("staff_email")?.Value
        };

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, options);

        Assert.Equal("custom-42", recorder.String(LoggingContract.UserId));
        Assert.Equal("delegate@example.com", recorder.String(LoggingContract.UserEmail));
    }

    [Fact]
    public void The_user_agent_is_capped_at_512_characters()
    {
        var context = Request();
        context.Request.Headers[HeaderNames.UserAgent] = new string('u', 900);

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, new RequestLogOptions());

        Assert.Equal(512, recorder.String(LoggingContract.UserAgent)!.Length);
    }

    [Fact]
    public void The_referer_reaches_the_summary_sanitised()
    {
        var context = Request();
        context.Request.Headers[HeaderNames.Referer] = "https://crm.example.com/auth/reset?id=live-token-value";

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, new RequestLogOptions());

        Assert.Equal("https://crm.example.com/auth/reset", recorder.String(LoggingContract.Referer));
    }

    [Fact]
    public void The_forwarded_for_claim_is_the_raw_address_not_the_reshaped_one()
    {
        // ClientIp is reshaped for a reader; ForwardedFor is the claim, and a claim rewritten
        // before it is recorded is not evidence of anything.
        var recorder = new RecordingDiagnosticContext();

        RequestSummaryEnricher.Enrich(recorder, Request(), new RequestLogOptions());

        Assert.Equal("203.0.113.99", recorder.String(LoggingContract.ClientIp));
        Assert.Equal("::ffff:203.0.113.99", recorder.String(LoggingContract.ForwardedFor));
    }

    [Fact]
    public void An_absent_header_sets_no_property()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("crm.example.com");

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, new RequestLogOptions());

        Assert.DoesNotContain(LoggingContract.Referer, recorder.Properties.Keys);
        Assert.DoesNotContain(LoggingContract.UserAgent, recorder.Properties.Keys);
        Assert.DoesNotContain(LoggingContract.ForwardedFor, recorder.Properties.Keys);
        Assert.DoesNotContain(LoggingContract.ClientIp, recorder.Properties.Keys);
    }

    [Fact]
    public void The_trace_id_reaches_the_summary()
    {
        using var activity = new Activity("request").Start();

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, Request(), new RequestLogOptions());

        Assert.Equal(activity.TraceId.ToString(), recorder.String(LoggingContract.TraceId));
    }

    [Fact]
    public void The_delegate_overload_reads_options_out_of_the_request_services()
    {
        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging(options => options.MaximumUserAgentLength = 8);

        var context = Request();
        context.RequestServices = services.BuildServiceProvider();

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.EnrichRequestSummary(recorder, context);

        Assert.Equal("Mozilla/", recorder.String(LoggingContract.UserAgent));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_claim_holding_no_value_sets_no_property(string claimValue)
    {
        // Blank is absent, and it has to be absent at BOTH call sites. The log context used to
        // test for null and the summary for emptiness, so one request answered the same
        // question two ways and one of the answers was a property nobody can filter on.
        var context = Request();
        context.User = Principal(
            new Claim(ClaimTypes.NameIdentifier, claimValue),
            new Claim(ClaimTypes.Email, claimValue));

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, new RequestLogOptions());

        Assert.DoesNotContain(LoggingContract.UserId, recorder.Properties.Keys);
        Assert.DoesNotContain(LoggingContract.UserEmail, recorder.Properties.Keys);
    }

    [Fact]
    public void A_selector_returning_an_empty_string_sets_no_property_either()
    {
        // Switching to a selector must not change what an absent user looks like.
        var context = Request(authenticated: true);

        var options = new RequestLogOptions
        {
            UserIdSelector = _ => string.Empty,
            UserEmailSelector = _ => "  "
        };

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, options);

        Assert.DoesNotContain(LoggingContract.UserId, recorder.Properties.Keys);
        Assert.DoesNotContain(LoggingContract.UserEmail, recorder.Properties.Keys);
    }

    [Fact]
    public void A_blank_email_claim_still_reaches_the_identity_name_fallback()
    {
        var context = Request();
        context.User = Principal(
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Email, "   "),
            new Claim(ClaimTypes.Name, "old-cookie@example.com"));

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, new RequestLogOptions());

        Assert.Equal("old-cookie@example.com", recorder.String(LoggingContract.UserEmail));
    }

    [Fact]
    public void An_identifier_with_content_is_never_trimmed()
    {
        // Blank means absent; it does not mean "tidy up the ones that have content". Trimming
        // an identifier is how you stop matching it.
        var context = Request(authenticated: true);

        var options = new RequestLogOptions { UserIdSelector = _ => " 42 " };

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, options);

        Assert.Equal(" 42 ", recorder.String(LoggingContract.UserId));
    }

    [Fact]
    public void An_authenticated_request_with_no_id_claim_carries_only_the_email()
    {
        // A signed-in principal is not a promise of either user property. A contract test that
        // demanded both would fail here on a request that is doing nothing wrong.
        var context = Request();
        context.User = Principal(new Claim(ClaimTypes.Email, "someone@example.com"));

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, new RequestLogOptions());

        Assert.DoesNotContain(LoggingContract.UserId, recorder.Properties.Keys);
        Assert.Equal("someone@example.com", recorder.String(LoggingContract.UserEmail));
    }

    [Fact]
    public void An_anonymous_request_without_optional_headers_carries_only_what_it_has()
    {
        // A health probe or an API client: no referer, no user agent, no forwarding header and
        // nobody signed in. Everything absent here is in LoggingContract.OptionalProperties,
        // which is what stops a consumer asserting the whole contract on traffic like this.
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.example.com");
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context, new RequestLogOptions());

        Assert.Equal(
            new[] { LoggingContract.ClientIp, LoggingContract.RequestHost, LoggingContract.RequestScheme }.Order(),
            recorder.Properties.Keys.Order());

        Assert.All(
            LoggingContract.EnricherProperties.Except(recorder.Properties.Keys),
            name => Assert.Contains(name, LoggingContract.OptionalProperties));
    }
}
