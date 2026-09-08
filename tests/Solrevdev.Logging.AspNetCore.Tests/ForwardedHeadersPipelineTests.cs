using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// The forwarded-header tests that use the REAL middleware.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ForwardedForHeaderTests"/> hands the helper a dictionary that looks like
/// middleware output. That is worth having and it is not the same thing as being right: the
/// dictionary is written from the same belief the code was written from, so the two agree
/// whether or not the belief holds. These run <c>ForwardedHeadersMiddleware</c> itself over a
/// request, in the documented pipeline order, and assert on real Serilog events.
/// </para>
/// <para>
/// Every address here is from a documentation range, per rule 12. <c>203.0.113.10</c> and
/// <c>203.0.113.11</c> stand in for load balancer hops, <c>203.0.113.42</c> for the client,
/// <c>203.0.113.66</c> for an entry a caller forged, and <c>203.0.113.90</c> for a caller
/// reaching the origin directly.
/// </para>
/// </remarks>
[Collection(SerilogLoggerCollection.Name)]
public class ForwardedHeadersPipelineTests
{
    private const string Proxy = "203.0.113.10";
    private const string SecondProxy = "203.0.113.11";
    private const string Client = "203.0.113.42";
    private const string Forged = "203.0.113.66";
    private const string DirectCaller = "203.0.113.90";

    private const string InnerMessage = "inside the request";
    private const string SummaryMessage = "the request summary";

    /// <summary>
    /// What one run through the pipeline produced: the events raised inside it and the
    /// properties the summary enricher set.
    /// </summary>
    private sealed record Run(
        List<LogEvent> Events,
        RecordingDiagnosticContext Summary,
        HttpContext Context)
    {
        public LogEvent Inner => Single(InnerMessage);

        public LogEvent SummaryEvent => Single(SummaryMessage);

        private LogEvent Single(string message) =>
            Assert.Single(Events, e => e.MessageTemplate.Text == message);
    }

    private static ForwardedHeadersOptions Trusting(params string[] proxies)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = proxies.Length
        };

        // The defaults trust loopback, which is not what any of these tests are about. Trust
        // is stated explicitly here so a test that passes says which peers it trusted.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in proxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        return options;
    }

    /// <summary>
    /// Runs a request through forwarded headers, the client log context, the seam where
    /// Serilog's request logging writes its summary, and the user log context, in the order the
    /// README documents.
    /// </summary>
    private static async Task<Run> RunAsync(
        string peer,
        Action<HttpContext>? arrange = null,
        ForwardedHeadersOptions? forwarding = null,
        ClaimsPrincipal? user = null,
        bool refuseBeforeUserContext = false,
        RequestLogOptions? options = null)
    {
        options ??= new RequestLogOptions();

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.example.com");
        context.Request.Method = "GET";
        context.Request.Path = "/orders";
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (user is not null)
        {
            context.User = user;
        }

        arrange?.Invoke(context);

        var summary = new RecordingDiagnosticContext();
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        RequestDelegate handler = _ =>
        {
            Log.Information(InnerMessage);
            return Task.CompletedTask;
        };

        RequestDelegate userContext = ctx =>
            new UserLogContextMiddleware(handler, Options.Create(options)).InvokeAsync(ctx);

        // Stands in for a rate limiter or an exception filter: something between the two
        // context middleware that ends the request before the user one is ever reached.
        RequestDelegate belowSummary = refuseBeforeUserContext
            ? ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                Log.Information(InnerMessage);
                return Task.CompletedTask;
            }
            : userContext;

        // Where UseSerilogRequestLogging writes: after the rest of the pipeline has returned,
        // and still inside the client scope pushed above it.
        RequestDelegate requestLogging = async ctx =>
        {
            await belowSummary(ctx);
            RequestSummaryEnricher.Enrich(summary, ctx, options);
            Log.Information(SummaryMessage);
        };

        RequestDelegate clientContext = ctx =>
            new ClientLogContextMiddleware(requestLogging).InvokeAsync(ctx);

        RequestDelegate entry = forwarding is null
            ? clientContext
            : ctx => new ForwardedHeadersMiddleware(
                clientContext,
                NullLoggerFactory.Instance,
                Options.Create(forwarding)).Invoke(ctx);

        var previous = Log.Logger;
        Log.Logger = logger;

        try
        {
            await entry(context);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }

        return new Run(sink.Events, summary, context);
    }

    private static string? Property(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value)
            ? (value as ScalarValue)?.Value as string
            : null;

    [Fact]
    public async Task A_trusted_single_hop_logs_the_client_the_balancer_added()
    {
        // The production case: one load balancer, one appended entry.
        var run = await RunAsync(
            Proxy,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = Client,
            Trusting(Proxy));

        Assert.Equal(Client, Property(run.Inner, LoggingContract.ClientIp));
        Assert.Equal(Client, run.Summary.String(LoggingContract.ClientIp));
        Assert.Equal(Client, run.Summary.String(LoggingContract.ForwardedFor));
    }

    [Fact]
    public async Task A_forged_extra_entry_does_not_become_the_client_address()
    {
        // The caller writes an address of their choosing to the LEFT of the one the balancer
        // will append. With one hop trusted, only the balancer's entry is taken.
        var run = await RunAsync(
            Proxy,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = $"{Forged},{Client}",
            Trusting(Proxy));

        Assert.Equal(Client, Property(run.Inner, LoggingContract.ClientIp));
        Assert.Equal(Client, run.Summary.String(LoggingContract.ClientIp));

        // The forgery is still RECORDED, which is the whole reason ForwardedFor exists: the
        // leftover entry is put back beside the address the framework settled on.
        Assert.Equal($"{Forged},{Client}", run.Summary.String(LoggingContract.ForwardedFor));
    }

    [Fact]
    public async Task An_untrusted_peer_is_logged_as_itself_however_it_labels_the_request()
    {
        var run = await RunAsync(
            DirectCaller,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = Forged,
            Trusting(Proxy));

        Assert.Equal(DirectCaller, Property(run.Inner, LoggingContract.ClientIp));
        Assert.Equal(DirectCaller, run.Summary.String(LoggingContract.ClientIp));
        Assert.Equal(Forged, run.Summary.String(LoggingContract.ForwardedFor));
    }

    [Fact]
    public async Task A_forged_original_for_invents_a_forwarding_claim_and_cannot_move_the_address()
    {
        // The measured limit of the reconstruction. X-Original-For is a header, so anybody can
        // send it, and the helper reads its presence as the middleware having acted. An
        // untrusted caller sending it and nothing else is reported as having forwarded itself.
        // ClientIp is unaffected: it comes off the connection.
        var run = await RunAsync(
            DirectCaller,
            ctx => ctx.Request.Headers["X-Original-For"] = "[203.0.113.66]:49725",
            Trusting(Proxy));

        Assert.Equal(DirectCaller, Property(run.Inner, LoggingContract.ClientIp));
        Assert.Equal(DirectCaller, run.Summary.String(LoggingContract.ForwardedFor));
    }

    [Fact]
    public async Task Without_forwarded_header_processing_the_balancer_is_logged_as_the_client()
    {
        // Middleware ORDER alone proves nothing. With no forwarded-header processing enabled,
        // every request through the balancer logs the balancer, and the pipeline looks right.
        var run = await RunAsync(
            Proxy,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = Client);

        Assert.Equal(Proxy, Property(run.Inner, LoggingContract.ClientIp));
        Assert.Equal(Proxy, run.Summary.String(LoggingContract.ClientIp));
        Assert.Equal(Client, run.Summary.String(LoggingContract.ForwardedFor));
    }

    [Fact]
    public async Task A_balancer_arriving_on_the_dual_stack_socket_is_still_trusted()
    {
        // Kestrel hands an IPv4 peer over as ::ffff:203.0.113.10 on a dual-stack socket.
        var run = await RunAsync(
            $"::ffff:{Proxy}",
            ctx => ctx.Request.Headers["X-Forwarded-For"] = Client,
            Trusting(Proxy));

        Assert.Equal(Client, Property(run.Inner, LoggingContract.ClientIp));
    }

    [Fact]
    public async Task A_mapped_client_entry_is_logged_in_its_dotted_form()
    {
        var run = await RunAsync(
            Proxy,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = $"::ffff:{Client}",
            Trusting(Proxy));

        // ClientIp is reshaped for a reader; ForwardedFor keeps the framework's spelling of
        // the entry, which is how the two differ without anybody forging anything.
        Assert.Equal(Client, run.Summary.String(LoggingContract.ClientIp));
        Assert.Equal($"::ffff:{Client}", run.Summary.String(LoggingContract.ForwardedFor));
    }

    [Fact]
    public async Task A_genuine_ipv6_client_is_logged_whole_rather_than_truncated()
    {
        // Without the mapped guard in ClientAddress, MapToIPv4 would report this caller as
        // 3.112.115.52: plausible, routable and never used by anybody involved.
        var run = await RunAsync(
            Proxy,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = "2001:db8:85a3::8a2e:370:7334",
            Trusting(Proxy));

        Assert.Equal("2001:db8:85a3::8a2e:370:7334", Property(run.Inner, LoggingContract.ClientIp));
        Assert.Equal("2001:db8:85a3::8a2e:370:7334", run.Summary.String(LoggingContract.ClientIp));
    }

    [Fact]
    public async Task A_client_entry_with_a_port_keeps_the_address_and_loses_the_port()
    {
        // An AWS balancer can append the client port. The framework puts it on
        // Connection.RemotePort, so the address is right and the reconstruction cannot get the
        // port back. A documented loss rather than a mismatch worth chasing.
        var run = await RunAsync(
            Proxy,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = $"{Client}:54321",
            Trusting(Proxy));

        Assert.Equal(Client, run.Summary.String(LoggingContract.ClientIp));
        Assert.Equal(54321, run.Context.Connection.RemotePort);
        Assert.Equal(Client, run.Summary.String(LoggingContract.ForwardedFor));
    }

    [Fact]
    public async Task Two_trusted_hops_settle_the_client_and_lose_the_middle_one()
    {
        // Client address correctness and ForwardedFor completeness are different questions and
        // this is the case that separates them. Both hops are consumed, so the connection holds
        // the client, and the intermediate address is gone from the reconstruction entirely.
        var run = await RunAsync(
            Proxy,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = $"{Client},{SecondProxy}",
            Trusting(Proxy, SecondProxy));

        Assert.Equal(Client, Property(run.Inner, LoggingContract.ClientIp));
        Assert.Equal(Client, run.Summary.String(LoggingContract.ClientIp));
        Assert.Equal(Client, run.Summary.String(LoggingContract.ForwardedFor));
    }

    [Fact]
    public async Task A_request_refused_before_the_user_context_still_carries_the_address()
    {
        // The reason for the two-middleware split. Something short-circuits above
        // UseAuthentication and the user middleware never runs; the address and the trace id
        // have to be on the event anyway, because a 429 with neither is the event somebody
        // goes looking for.
        using var activity = new Activity("request").Start();

        var run = await RunAsync(
            Proxy,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = Client,
            Trusting(Proxy),
            refuseBeforeUserContext: true);

        Assert.Equal(StatusCodes.Status429TooManyRequests, run.Context.Response.StatusCode);
        Assert.Equal(Client, Property(run.Inner, LoggingContract.ClientIp));
        Assert.Equal(activity.TraceId.ToString(), Property(run.Inner, LoggingContract.TraceId));
        Assert.DoesNotContain(LoggingContract.UserId, run.Inner.Properties.Keys);
    }

    [Fact]
    public async Task The_summary_event_is_inside_the_client_scope_and_outside_the_user_one()
    {
        // Which scope is still open when the summary is written is the reason the enricher
        // sets these properties itself. The client scope wraps request logging, so its
        // properties are still on the event; the user scope is below it and has ended, so the
        // user reaches the summary through the diagnostic context or not at all.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "42")],
            "TestAuth"));

        var run = await RunAsync(
            Proxy,
            ctx => ctx.Request.Headers["X-Forwarded-For"] = Client,
            Trusting(Proxy),
            user: user);

        Assert.Equal(Client, Property(run.SummaryEvent, LoggingContract.ClientIp));
        Assert.DoesNotContain(LoggingContract.UserId, run.SummaryEvent.Properties.Keys);
        Assert.Equal("42", Property(run.Inner, LoggingContract.UserId));
        Assert.Equal("42", run.Summary.String(LoggingContract.UserId));
    }
}
