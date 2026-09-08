using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Serilog.Events;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Pins how the two short overloads find their options, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequestLogLevel.For(HttpContext, Exception?)"/> and
/// <see cref="RequestSummaryEnricher.EnrichRequestSummary"/> read
/// <c>HttpContext.RequestServices</c> and fall back to a default
/// <see cref="RequestLogOptions"/> when there is nothing to read. The fallback is correct and
/// it is silent, which is the part that costs time: a bare <c>DefaultHttpContext</c> has no
/// <c>RequestServices</c>, so a test written against these overloads measures the package
/// defaults while reading as though it measures the application's configuration. Somebody
/// integrating this package measured exactly that and concluded the overloads never consult
/// DI at all.
/// </para>
/// <para>
/// So both halves are asserted here rather than one: with a populated
/// <c>RequestServices</c> the application's configuration wins, and without one the defaults
/// are used. The second is a stated guarantee, not an accident.
/// </para>
/// </remarks>
public class OptionsResolutionTests
{
    private static IServiceProvider Configured(Action<RequestLogOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging(configure);

        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext Request(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.example.com");
        context.Request.Path = path;
        context.Request.Headers[HeaderNames.UserAgent] = "Mozilla/5.0 with a long tail";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");

        return context;
    }

    [Fact]
    public void The_level_overload_reads_the_applications_quiet_paths_out_of_request_services()
    {
        var context = Request("/version");
        context.RequestServices = Configured(options => options.QuietPathPrefixes.Clear());

        // The application decided /version is worth a line. Without the registration reaching
        // the call, the package default would quieten it.
        Assert.Equal(LogEventLevel.Information, RequestLogLevel.For(context, null));
    }

    [Fact]
    public void The_level_overload_uses_package_defaults_when_there_are_no_request_services()
    {
        // A bare DefaultHttpContext. This is the measurement that reads like a bug and is not
        // one: /version is quiet because the PACKAGE says so, whatever the application says.
        var context = Request("/version");

        Assert.Null(context.RequestServices);
        Assert.Equal(LogEventLevel.Verbose, RequestLogLevel.For(context, null));

        // And the same call against the application's options gives the application's answer,
        // which is how a test asks the question it meant to ask.
        var options = new RequestLogOptions();
        options.QuietPathPrefixes.Clear();

        Assert.Equal(LogEventLevel.Information, RequestLogLevel.For(context, null, options));
    }

    [Fact]
    public void The_level_overload_uses_package_defaults_when_the_options_are_not_registered()
    {
        // A provider with nothing registered at all. UseSolrevdevClientLogContext and its two
        // siblings now refuse to wire in this state, so an application cannot reach here; a
        // test or a hand-built provider still can, and it gets the documented defaults.
        var context = Request("/version");
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        Assert.Equal(LogEventLevel.Verbose, RequestLogLevel.For(context, null));
    }

    [Fact]
    public void The_enricher_overload_reads_the_applications_caps_out_of_request_services()
    {
        var context = Request("/orders");
        context.RequestServices = Configured(options => options.MaximumUserAgentLength = 7);

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.EnrichRequestSummary(recorder, context);

        Assert.Equal("Mozilla", recorder.String(LoggingContract.UserAgent));
    }

    [Fact]
    public void The_enricher_overload_uses_package_defaults_when_there_are_no_request_services()
    {
        var context = Request("/orders");

        Assert.Null(context.RequestServices);

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.EnrichRequestSummary(recorder, context);

        Assert.Equal("Mozilla/5.0 with a long tail", recorder.String(LoggingContract.UserAgent));
    }

    [Fact]
    public void The_enricher_overload_uses_package_defaults_when_the_options_are_not_registered()
    {
        var context = Request("/orders");
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        var recorder = new RecordingDiagnosticContext();
        RequestSummaryEnricher.EnrichRequestSummary(recorder, context);

        Assert.Equal("Mozilla/5.0 with a long tail", recorder.String(LoggingContract.UserAgent));
    }
}
