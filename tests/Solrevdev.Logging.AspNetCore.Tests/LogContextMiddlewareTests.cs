using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// The two middleware are split because of where each has to sit. These assert what each one
/// actually puts on an event, which is the half a pipeline-order test in a consuming
/// application cannot see.
/// </summary>
[Collection(SerilogLoggerCollection.Name)]
public class LogContextMiddlewareTests
{
    private static async Task<LogEvent> EventRaisedInside(Func<RequestDelegate, HttpContext, Task> run, HttpContext context)
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var previous = Log.Logger;
        Log.Logger = logger;

        try
        {
            await run(_ =>
            {
                Log.Information("inside the request");
                return Task.CompletedTask;
            }, context);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }

        return Assert.Single(sink.Events);
    }

    private static string? Property(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value)
            ? (value as ScalarValue)?.Value as string
            : null;

    [Fact]
    public async Task The_client_context_carries_the_address_and_the_trace_id()
    {
        using var activity = new Activity("request").Start();

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::1");

        var logEvent = await EventRaisedInside(
            (next, ctx) => new ClientLogContextMiddleware(next).InvokeAsync(ctx),
            context);

        Assert.Equal("127.0.0.1", Property(logEvent, LoggingContract.ClientIp));
        Assert.Equal(activity.TraceId.ToString(), Property(logEvent, LoggingContract.TraceId));
    }

    [Fact]
    public async Task The_client_context_carries_no_address_when_there_is_none()
    {
        var logEvent = await EventRaisedInside(
            (next, ctx) => new ClientLogContextMiddleware(next).InvokeAsync(ctx),
            new DefaultHttpContext());

        Assert.DoesNotContain(LoggingContract.ClientIp, logEvent.Properties.Keys);
    }

    [Fact]
    public async Task An_anonymous_request_carries_no_user_properties()
    {
        var logEvent = await EventRaisedInside(
            (next, ctx) => Middleware(next).InvokeAsync(ctx),
            new DefaultHttpContext());

        Assert.DoesNotContain(LoggingContract.UserId, logEvent.Properties.Keys);
        Assert.DoesNotContain(LoggingContract.UserEmail, logEvent.Properties.Keys);
    }

    [Fact]
    public async Task An_authenticated_request_carries_both_user_properties()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "42"),
                    new Claim(ClaimTypes.Email, "someone@example.com")
                ],
                "TestAuth"))
        };

        var logEvent = await EventRaisedInside(
            (next, ctx) => Middleware(next).InvokeAsync(ctx),
            context);

        Assert.Equal("42", Property(logEvent, LoggingContract.UserId));
        Assert.Equal("someone@example.com", Property(logEvent, LoggingContract.UserEmail));
    }

    [Fact]
    public async Task A_missing_claim_sets_no_property_rather_than_an_empty_one()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "42")],
                "TestAuth"))
        };

        var logEvent = await EventRaisedInside(
            (next, ctx) => Middleware(next, new RequestLogOptions { FallBackToIdentityNameForEmail = false })
                .InvokeAsync(ctx),
            context);

        Assert.Equal("42", Property(logEvent, LoggingContract.UserId));
        Assert.DoesNotContain(LoggingContract.UserEmail, logEvent.Properties.Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_claim_holding_no_value_pushes_no_property(string claimValue)
    {
        // The measured mismatch. An authenticated NameIdentifier claim holding an empty string
        // used to push UserId="" onto every event inside the request while the request summary
        // omitted the property, so one request said two different things and the log-context
        // answer was the one nobody can filter on.
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, claimValue)],
                "TestAuth"))
        };

        var logEvent = await EventRaisedInside(
            (next, ctx) => Middleware(next, new RequestLogOptions { FallBackToIdentityNameForEmail = false })
                .InvokeAsync(ctx),
            context);

        Assert.DoesNotContain(LoggingContract.UserId, logEvent.Properties.Keys);
        Assert.DoesNotContain(LoggingContract.UserEmail, logEvent.Properties.Keys);
    }

    [Fact]
    public async Task A_selector_returning_an_empty_string_pushes_no_property()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "42")],
                "TestAuth"))
        };

        var options = new RequestLogOptions
        {
            UserIdSelector = _ => string.Empty,
            UserEmailSelector = _ => null
        };

        var logEvent = await EventRaisedInside(
            (next, ctx) => Middleware(next, options).InvokeAsync(ctx),
            context);

        Assert.DoesNotContain(LoggingContract.UserId, logEvent.Properties.Keys);
    }

    private static UserLogContextMiddleware Middleware(RequestDelegate next, RequestLogOptions? options = null) =>
        new(next, Options.Create(options ?? new RequestLogOptions()));
}
