using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Extensions.Hosting;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// The seam for an application's own summary fields, run through Serilog's REAL request
/// logging middleware as <c>UseSolrevdevRequestLogging</c> wires it.
/// </summary>
/// <remarks>
/// <para>
/// Before 1.3.0 an application with a field of its own had to call
/// <c>UseSerilogRequestLogging</c> itself and wire the package's enricher and level function by
/// hand, which skips the startup guard. Two of the three applications did.
/// </para>
/// <para>
/// The logger is handed to the middleware through its own options, so nothing here touches the
/// process-global <c>Log.Logger</c>. One test enables the process-global <c>SelfLog</c>, so
/// the class sits in the shared collection anyway.
/// </para>
/// </remarks>
[Collection(SerilogLoggerCollection.Name)]
public class SummarySeamTests
{
    private const string AppField = "OrderReference";

    private sealed record Run(List<LogEvent> Events, HttpContext Context, Exception? Escaped)
    {
        public LogEvent Summary => Assert.Single(Events);
    }

    /// <summary>
    /// Builds the pipeline the way an application does and sends one request through it. The
    /// request has NO <c>RequestServices</c> unless <paramref name="arrange"/> gives it one.
    /// </summary>
    private static async Task<Run> RunAsync(
        Action<RequestLogOptions> configure,
        Action<Serilog.AspNetCore.RequestLoggingOptions>? configureSerilog = null,
        Action<HttpContext>? arrange = null,
        string path = "/orders")
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging(configure);
        services.AddSingleton(new DiagnosticContext(logger));

        var app = new ApplicationBuilder(services.BuildServiceProvider());
        app.UseSolrevdevRequestLogging(options =>
        {
            options.Logger = logger;
            configureSerilog?.Invoke(options);
        });
        app.Run(_ => Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.example.com");
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Headers[HeaderNames.UserAgent] = "Mozilla/5.0 with a long tail";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "42"), new Claim(ClaimTypes.Email, "someone@example.com")],
            "TestAuth"));
        arrange?.Invoke(context);

        Exception? escaped = null;
        try
        {
            await app.Build()(context);
        }
        catch (Exception exception)
        {
            escaped = exception;
        }

        return new Run(sink.Events, context, escaped);
    }

    private static string? Property(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value)
            ? (value as ScalarValue)?.Value as string
            : null;

    [Fact]
    public async Task An_application_enricher_adds_its_field_to_the_summary()
    {
        var run = await RunAsync(options =>
            options.SummaryEnrichers.Add((diagnostics, _) => diagnostics.Set(AppField, "ORD-000412")));

        Assert.Null(run.Escaped);
        Assert.Equal("ORD-000412", Property(run.Summary, AppField));
        Assert.Equal("203.0.113.42", Property(run.Summary, LoggingContract.ClientIp));
        Assert.Equal("42", Property(run.Summary, LoggingContract.UserId));
    }

    [Fact]
    public void Application_enrichers_run_after_the_packages_own_and_in_the_order_added()
    {
        var order = new List<string>();
        var seenByFirst = new List<string>();
        var recorder = new RecordingDiagnosticContext();

        var options = new RequestLogOptions { IncludeNavigation = true };
        options.SummaryEnrichers.Add((diagnostics, _) =>
        {
            // Everything the package sets is already there when the first of these runs.
            seenByFirst.AddRange(recorder.Properties.Keys);
            order.Add("first");
        });
        options.SummaryEnrichers.Add((_, _) => order.Add("second"));

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.example.com");
        context.Request.Headers[HeaderNames.UserAgent] = "Mozilla/5.0";
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        context.Request.Headers["Sec-Fetch-Site"] = "none";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "42")], "TestAuth"));

        RequestSummaryEnricher.Enrich(recorder, context, options);

        Assert.Equal(["first", "second"], order);
        Assert.Equal(
            new[]
            {
                LoggingContract.Navigation, LoggingContract.RequestHost, LoggingContract.RequestScheme,
                LoggingContract.UserAgent, LoggingContract.UserId
            },
            seenByFirst.Order());
    }

    [Fact]
    public async Task An_application_enricher_that_throws_cannot_drop_the_packages_properties()
    {
        var selfLog = new List<string>();
        SelfLog.Enable(message =>
        {
            lock (selfLog)
            {
                selfLog.Add(message);
            }
        });

        Run run;
        try
        {
            run = await RunAsync(options =>
            {
                options.SummaryEnrichers.Add((_, _) => throw new InvalidOperationException("the first one is broken"));
                options.SummaryEnrichers.Add((diagnostics, _) => diagnostics.Set(AppField, "ORD-000412"));
            });
        }
        finally
        {
            SelfLog.Disable();
        }

        // The request did not fail, the line was written, the package's properties are on it,
        // and the enricher after the broken one still ran.
        Assert.Null(run.Escaped);
        Assert.Equal("203.0.113.42", Property(run.Summary, LoggingContract.ClientIp));
        Assert.Equal("Mozilla/5.0 with a long tail", Property(run.Summary, LoggingContract.UserAgent));
        Assert.Equal("app.example.com", Property(run.Summary, LoggingContract.RequestHost));
        Assert.Equal("https", Property(run.Summary, LoggingContract.RequestScheme));
        Assert.Equal("42", Property(run.Summary, LoggingContract.UserId));
        Assert.Equal("someone@example.com", Property(run.Summary, LoggingContract.UserEmail));
        Assert.Equal("ORD-000412", Property(run.Summary, AppField));

        // And it was not silent.
        lock (selfLog)
        {
            Assert.Contains(selfLog, message =>
                message.Contains("summary enricher 0 threw", StringComparison.Ordinal)
                && message.Contains("the first one is broken", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task What_swallowing_avoids_a_throwing_delegate_fails_the_request_and_writes_no_line()
    {
        // THE MEASUREMENT behind the decision to catch. This replaces EnrichDiagnosticContext
        // through the Serilog options, which is what an application's hand-wired delegate is,
        // and lets it throw. Serilog's middleware calls the delegate after the page has run:
        // the exception escapes the middleware and no summary event is written at all. If a
        // later Serilog.AspNetCore starts catching it, this fails and the comment on
        // RequestLogOptions.SummaryEnrichers wants rewording.
        var run = await RunAsync(
            _ => { },
            serilog => serilog.EnrichDiagnosticContext =
                (_, _) => throw new InvalidOperationException("thrown from the delegate"));

        Assert.IsType<InvalidOperationException>(run.Escaped);
        Assert.Empty(run.Events);
    }

    [Fact]
    public async Task A_null_entry_in_the_list_is_skipped()
    {
        var run = await RunAsync(options =>
        {
            options.SummaryEnrichers.Add(null!);
            options.SummaryEnrichers.Add((diagnostics, _) => diagnostics.Set(AppField, "ORD-000412"));
        });

        Assert.Null(run.Escaped);
        Assert.Equal("ORD-000412", Property(run.Summary, AppField));
    }

    [Fact]
    public async Task The_helper_uses_the_applications_options_with_no_request_services_at_all()
    {
        // The recorded reason one application bypassed the helper was that the short overloads
        // read package defaults. They do only when HttpContext.RequestServices is null, which
        // a host never leaves it. From 1.3.0 the helper does not ask the request at all: the
        // cap, the selector and the quiet paths below all come from the registration.
        var run = await RunAsync(
            options =>
            {
                options.MaximumUserAgentLength = 7;
                options.UserIdSelector = _ => "from-the-selector";
                options.QuietPathPrefixes.Clear();
            },
            arrange: context => Assert.Null(context.RequestServices),
            path: "/version");

        Assert.Equal("Mozilla", Property(run.Summary, LoggingContract.UserAgent));
        Assert.Equal("from-the-selector", Property(run.Summary, LoggingContract.UserId));

        // /version is quiet by package default. The application cleared the list, so its
        // summary is worth a line.
        Assert.Equal(LogEventLevel.Information, run.Summary.Level);
    }

    [Fact]
    public async Task The_helper_still_demotes_a_quiet_path_by_default()
    {
        var run = await RunAsync(_ => { }, path: "/version");

        Assert.Equal(LogEventLevel.Verbose, run.Summary.Level);
    }

    [Fact]
    public async Task The_configure_callback_still_runs_last_and_can_still_replace_the_delegates()
    {
        // Unchanged from 1.2.0, and still documented as undoing the point of the helper.
        var run = await RunAsync(
            _ => { },
            serilog =>
            {
                serilog.EnrichDiagnosticContext = (diagnostics, _) => diagnostics.Set(AppField, "replaced");
                serilog.GetLevel = (_, _, _) => LogEventLevel.Warning;
            });

        Assert.Equal("replaced", Property(run.Summary, AppField));
        Assert.Null(Property(run.Summary, LoggingContract.ClientIp));
        Assert.Equal(LogEventLevel.Warning, run.Summary.Level);
    }

    [Fact]
    public void The_short_overload_runs_the_applications_enrichers_too()
    {
        // An application that keeps its hand-wired UseSerilogRequestLogging can still move its
        // fields into the options: the delegate reads them out of the request services.
        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging(options =>
            options.SummaryEnrichers.Add((diagnostics, _) => diagnostics.Set(AppField, "ORD-000412")));

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var recorder = new RecordingDiagnosticContext();

        RequestSummaryEnricher.EnrichRequestSummary(recorder, context);

        Assert.Equal("ORD-000412", recorder.String(AppField));
    }

    [Fact]
    public void There_are_no_application_enrichers_by_default()
    {
        Assert.Empty(new RequestLogOptions().SummaryEnrichers);
    }
}
