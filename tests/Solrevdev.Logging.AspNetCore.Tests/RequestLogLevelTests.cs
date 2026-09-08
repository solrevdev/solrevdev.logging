using Microsoft.AspNetCore.Http;
using Serilog.Events;

namespace Solrevdev.Logging.AspNetCore.Tests;

public class RequestLogLevelTests
{
    private static HttpContext Request(string path, int statusCode = 200)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.StatusCode = statusCode;
        return context;
    }

    private static readonly RequestLogOptions Options = new();

    [Theory]
    [InlineData("/css/site.css")]
    [InlineData("/js/app.mjs")]
    [InlineData("/lib/bootstrap.min.js")]
    [InlineData("/images/logo.svg")]
    [InlineData("/fonts/inter.woff2")]
    [InlineData("/favicon.ico")]
    [InlineData("/site.webmanifest")]
    [InlineData("/healthcheck")]
    [InlineData("/version")]
    public void A_successful_quiet_path_drops_to_verbose(string path)
    {
        Assert.Equal(LogEventLevel.Verbose, RequestLogLevel.For(Request(path), null, Options));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/admin/orders")]
    [InlineData("/api/shipments/1")]
    public void A_real_page_keeps_its_information_event(string path)
    {
        Assert.Equal(LogEventLevel.Information, RequestLogLevel.For(Request(path), null, Options));
    }

    [Fact]
    public void A_missing_stylesheet_keeps_its_information_event()
    {
        // Hiding this is how a half-copied deploy looks like a working one.
        Assert.Equal(
            LogEventLevel.Information,
            RequestLogLevel.For(Request("/css/site.css", 404), null, Options));
    }

    [Fact]
    public void A_failing_health_probe_is_an_error()
    {
        Assert.Equal(
            LogEventLevel.Error,
            RequestLogLevel.For(Request("/healthcheck", 500), null, Options));
    }

    [Fact]
    public void An_exception_is_an_error_even_on_a_quiet_path()
    {
        Assert.Equal(
            LogEventLevel.Error,
            RequestLogLevel.For(Request("/css/site.css"), new InvalidOperationException(), Options));
    }

    [Fact]
    public void A_prefix_is_matched_by_segment_not_by_string()
    {
        // "/versionhistory" is a real page, and a raw StartsWith would have quietened it.
        Assert.Equal(
            LogEventLevel.Information,
            RequestLogLevel.For(Request("/versionhistory"), null, Options));
    }

    [Fact]
    public void A_deeper_probe_path_is_still_quiet()
    {
        Assert.Equal(
            LogEventLevel.Verbose,
            RequestLogLevel.For(Request("/healthcheck/ready"), null, Options));
    }

    [Fact]
    public void The_quiet_paths_are_configurable()
    {
        var options = new RequestLogOptions();
        options.QuietPathPrefixes.Clear();
        options.QuietPathPrefixes.Add("/metrics");

        Assert.Equal(LogEventLevel.Verbose, RequestLogLevel.For(Request("/metrics"), null, options));
        Assert.Equal(LogEventLevel.Information, RequestLogLevel.For(Request("/healthcheck"), null, options));
    }

    [Fact]
    public void Static_file_demotion_can_be_switched_off()
    {
        var options = new RequestLogOptions();
        options.QuietFileExtensions.Clear();

        Assert.Equal(LogEventLevel.Information, RequestLogLevel.For(Request("/css/site.css"), null, options));
    }

    [Fact]
    public void Options_come_from_the_request_services_when_there_are_any()
    {
        // The no-services overload has to work too, because GetLevel is handed a context
        // whose RequestServices may be unset in a test host.
        Assert.Equal(LogEventLevel.Verbose, RequestLogLevel.For(Request("/css/site.css"), null));
    }
}
