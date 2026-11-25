using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Serilog;
using Serilog.Events;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// The three properties added in 1.3.0 are each off until an application switches one on.
/// These pin the off half as hard as the on half: an application that upgrades from 1.2.0 and
/// changes nothing must write exactly what it wrote before.
/// </summary>
[Collection(SerilogLoggerCollection.Name)]
public class OptInPropertiesTests
{
    /// <summary>
    /// A request that would produce all three opt-in properties if they were switched on.
    /// </summary>
    private static DefaultHttpContext Request()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.example.com");
        context.Request.Path = "/orders";
        context.Request.QueryString = new QueryString("?page=2&q=Jane%20Doe");
        context.Request.Headers[HeaderNames.UserAgent] = "Mozilla/5.0";
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        context.Request.Headers["Sec-Fetch-Dest"] = "document";
        context.Request.Headers["Sec-Fetch-User"] = "?1";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "page"));

        return context;
    }

    private static RecordingDiagnosticContext Enrich(RequestLogOptions options, HttpContext? context = null)
    {
        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.Enrich(recorder, context ?? Request(), options);

        return recorder;
    }

    [Fact]
    public void All_three_are_off_by_default()
    {
        var options = new RequestLogOptions();

        Assert.False(options.IncludeNavigation);
        Assert.False(options.IncludeRequestQuery);
        Assert.False(options.IncludeSpanId);
    }

    [Fact]
    public void Default_options_write_none_of_them_on_a_request_that_could_carry_all_three()
    {
        using var activity = new Activity("request").Start();

        var recorder = Enrich(new RequestLogOptions());

        Assert.Empty(recorder.Properties.Keys.Intersect(LoggingContract.ExtendedProperties));
        Assert.All(recorder.Properties.Keys, name => Assert.Contains(name, LoggingContract.AllProperties));
    }

    [Fact]
    public void Navigation_is_written_when_switched_on()
    {
        var recorder = Enrich(new RequestLogOptions { IncludeNavigation = true });

        Assert.Equal(RequestNavigation.User, recorder.String(LoggingContract.Navigation));
    }

    [Fact]
    public void Navigation_is_absent_when_the_caller_sent_no_fetch_headers()
    {
        var context = Request();
        context.Request.Headers.Remove("Sec-Fetch-Mode");
        context.Request.Headers.Remove("Sec-Fetch-Site");
        context.Request.Headers.Remove("Sec-Fetch-Dest");

        var recorder = Enrich(new RequestLogOptions { IncludeNavigation = true }, context);

        Assert.DoesNotContain(LoggingContract.Navigation, recorder.Properties.Keys);
    }

    [Fact]
    public void The_query_is_written_when_switched_on_and_withheld_until_a_key_is_listed()
    {
        var withheld = Enrich(new RequestLogOptions { IncludeRequestQuery = true });
        Assert.Equal("page=(set)&q=(set)", withheld.String(LoggingContract.RequestQuery));

        var options = new RequestLogOptions { IncludeRequestQuery = true };
        options.RequestQueryKeys.Add("page");
        Assert.Equal("page=2&q=(set)", Enrich(options).String(LoggingContract.RequestQuery));
    }

    [Fact]
    public void The_span_id_is_written_beside_the_trace_id_when_switched_on()
    {
        using var activity = new Activity("request").Start();

        var recorder = Enrich(new RequestLogOptions { IncludeSpanId = true });

        Assert.Equal(activity.SpanId.ToString(), recorder.String(LoggingContract.SpanId));
        Assert.Equal(activity.TraceId.ToString(), recorder.String(LoggingContract.TraceId));
        Assert.Equal(16, recorder.String(LoggingContract.SpanId)!.Length);
    }

    [Fact]
    public void The_span_id_is_absent_without_an_activity()
    {
        Assert.Null(Activity.Current);

        var recorder = Enrich(new RequestLogOptions { IncludeSpanId = true });

        Assert.DoesNotContain(LoggingContract.SpanId, recorder.Properties.Keys);
    }

    [Fact]
    public async Task The_client_context_pushes_the_span_id_only_when_switched_on()
    {
        using var activity = new Activity("request").Start();

        var off = await EventRaisedInside(Services(_ => { }));
        Assert.False(off.Properties.ContainsKey(LoggingContract.SpanId));
        Assert.True(off.Properties.ContainsKey(LoggingContract.TraceId));

        var on = await EventRaisedInside(Services(options => options.IncludeSpanId = true));
        Assert.Equal(activity.SpanId.ToString(), ((ScalarValue)on.Properties[LoggingContract.SpanId]).Value);
    }

    [Fact]
    public async Task The_client_context_pushes_no_span_id_without_request_services()
    {
        // A bare DefaultHttpContext, which is what every 1.2.0 test of this middleware uses.
        using var activity = new Activity("request").Start();

        var logged = await EventRaisedInside(requestServices: null);

        Assert.False(logged.Properties.ContainsKey(LoggingContract.SpanId));
        Assert.True(logged.Properties.ContainsKey(LoggingContract.TraceId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_query_cap_that_keeps_nothing_is_refused_at_startup(int cap)
    {
        var pairs = new ServiceCollection();
        pairs.AddSolrevdevRequestLogging(options => options.MaximumRequestQueryPairs = cap);
        var pairsError = Assert.Throws<OptionsValidationException>(
            () => pairs.BuildServiceProvider().GetRequiredService<IOptions<RequestLogOptions>>().Value);
        Assert.Contains(nameof(RequestLogOptions.MaximumRequestQueryPairs), pairsError.Message, StringComparison.Ordinal);

        var length = new ServiceCollection();
        length.AddSolrevdevRequestLogging(options => options.MaximumRequestQueryValueLength = cap);
        var lengthError = Assert.Throws<OptionsValidationException>(
            () => length.BuildServiceProvider().GetRequiredService<IOptions<RequestLogOptions>>().Value);
        Assert.Contains(nameof(RequestLogOptions.MaximumRequestQueryValueLength), lengthError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_query_defaults_are_the_documented_ones()
    {
        var options = new RequestLogOptions();

        Assert.Equal(12, options.MaximumRequestQueryPairs);
        Assert.Equal(50, options.MaximumRequestQueryValueLength);
        Assert.Empty(options.RequestQueryKeys);
        Assert.Equal("(set)", RequestQuery.Withheld);
        Assert.Equal("(other)", RequestQuery.OtherKey);
    }

    [Fact]
    public void The_header_cut_is_public_and_follows_the_same_rule_as_the_summary()
    {
        // An application writing UserAgent on an event of its own cuts it with the configured
        // cap, so one name never carries two lengths.
        var options = new RequestLogOptions { MaximumUserAgentLength = 7 };

        Assert.True(typeof(HeaderValue).IsPublic);
        Assert.Equal("Mozilla", HeaderValue.Truncate("  Mozilla/5.0  ", options.MaximumUserAgentLength));
        Assert.Null(HeaderValue.Truncate("   ", options.MaximumUserAgentLength));
        Assert.Null(HeaderValue.Truncate("Mozilla/5.0", 0));
    }

    private static IServiceProvider Services(Action<RequestLogOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging(configure);

        return services.BuildServiceProvider();
    }

    private static async Task<LogEvent> EventRaisedInside(IServiceProvider? requestServices)
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var context = new DefaultHttpContext();
        if (requestServices is not null)
        {
            context.RequestServices = requestServices;
        }

        var previous = Log.Logger;
        Log.Logger = logger;

        try
        {
            await new ClientLogContextMiddleware(_ =>
            {
                Log.Information("inside the request");
                return Task.CompletedTask;
            }).InvokeAsync(context);
        }
        finally
        {
            Log.Logger = previous;
            logger.Dispose();
        }

        return Assert.Single(sink.Events);
    }
}
