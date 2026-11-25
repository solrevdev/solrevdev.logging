using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// <c>web.antiforgery.refused</c> from outside MVC: minimal-API endpoints on a TestServer.
/// </summary>
/// <remarks>
/// A minimal-API endpoint that catches <see cref="AntiforgeryValidationException"/> is seen
/// by no global MVC filter, and without a public writer it has to copy the event's code.
/// <see cref="AntiforgeryRefusalLog"/> is the one implementation, and the first test here holds the MVC filter and a minimal-API caller to
/// the same properties by running both in one host.
/// </remarks>
public class AntiforgeryRefusalLogTests
{
    private sealed record Entry(LogLevel Level, string Category, string Message, IReadOnlyDictionary<string, object?> Properties);

    private sealed class Capture : ILoggerProvider
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public List<Entry> Refusals => _entries
            .Where(entry => entry.Properties.GetValueOrDefault(LoggingContract.Event) as string == RefusalEvents.WebAntiforgeryRefused)
            .ToList();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(Capture capture, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                    : [];

                capture._entries.Enqueue(new Entry(logLevel, category, formatter(state, exception), properties));
            }
        }
    }

    private sealed class Site(WebApplication app, Capture capture) : IAsyncDisposable
    {
        public Capture Capture => capture;

        public HttpClient Client { get; } = app.GetTestClient();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    /// <summary>What an endpoint that validates for itself looks like in an application.</summary>
    private static async Task<IResult> AddToOrder(HttpContext httpContext, IAntiforgery antiforgery, ILoggerFactory loggers)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException exception)
        {
            AntiforgeryRefusalLog.Write(loggers.CreateLogger("Orders"), httpContext, exception);
            return Results.BadRequest();
        }

        return Results.Ok();
    }

    private static async Task<Site> StartAsync(Action<WebApplication> map, int maximumUserAgentLength = 512)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AntiforgeryRefusalLogTests).Assembly.GetName().Name,
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();

        var capture = new Capture();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);
        builder.Services.AddSingleton(new Pages.FormScript());
        builder.Services.AddRazorPages();
        builder.Services.AddAntiforgery();
        builder.Services.AddSolrevdevRequestLogging(options => options.MaximumUserAgentLength = maximumUserAgentLength);
        builder.Services.AddSolrevdevRefusalLogging();

        var app = builder.Build();
        app.UseAntiforgery();
        app.MapRazorPages();
        map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        return new Site(app, capture);
    }

    private static Task<HttpResponseMessage> PostAsync(Site site, string path, params (string Name, string Value)[] fields) =>
        site.Client.PostAsync(
            path,
            new FormUrlEncodedContent(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value))),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_minimal_api_endpoint_writes_the_same_event_as_the_mvc_filter()
    {
        await using var site = await StartAsync(app => app.MapPost("/orders/add", AddToOrder), maximumUserAgentLength: 20);
        var longAgent = "TestBrowser/1.0 " + new string('a', 700);
        site.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", longAgent);

        using var page = await PostAsync(site, "/Guarded");
        using var endpoint = await PostAsync(site, "/orders/add", ("item", "nobody@example.com"));

        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, endpoint.StatusCode);

        var refusals = site.Capture.Refusals;
        Assert.Equal(2, refusals.Count);
        var fromMvc = Assert.Single(refusals, entry => (string?)entry.Properties[LoggingContract.RequestPath] == "/Guarded");
        var fromEndpoint = Assert.Single(refusals, entry => (string?)entry.Properties[LoggingContract.RequestPath] == "/orders/add");

        // Identical: the level, the template, the property names, and every value that does
        // not name the path.
        Assert.Equal(LogLevel.Warning, fromEndpoint.Level);
        Assert.Equal(fromMvc.Level, fromEndpoint.Level);
        Assert.Equal(fromMvc.Properties["{OriginalFormat}"], fromEndpoint.Properties["{OriginalFormat}"]);
        Assert.Equal(fromMvc.Properties.Keys.Order(), fromEndpoint.Properties.Keys.Order());
        foreach (var name in fromMvc.Properties.Keys.Where(name => name != LoggingContract.RequestPath))
        {
            Assert.Equal(fromMvc.Properties[name], fromEndpoint.Properties[name]);
        }

        Assert.Equal(RefusalEvents.WebAntiforgeryRefused, fromEndpoint.Properties[LoggingContract.Event]);
        Assert.Equal("POST", fromEndpoint.Properties[LoggingContract.RequestMethod]);
        Assert.Equal(false, fromEndpoint.Properties[LoggingContract.HasAntiforgeryCookie]);
        Assert.Equal(longAgent[..20], fromEndpoint.Properties[LoggingContract.UserAgent]);
        Assert.Contains("antiforgery", (string)fromEndpoint.Properties[LoggingContract.RefusalReason]!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nobody@example.com", fromEndpoint.Message, StringComparison.Ordinal);

        // The one difference is the caller's to choose: the logger it handed over.
        Assert.Equal("Orders", fromEndpoint.Category);
        Assert.Equal(typeof(AntiforgeryRefusalLogFilter).FullName, fromMvc.Category);
    }

    [Fact]
    public async Task The_endpoint_filter_logs_an_exception_the_endpoint_lets_out_and_throws_it_on()
    {
        await using var site = await StartAsync(app =>
            app.MapGroup("/orders")
                .AddEndpointFilter<AntiforgeryRefusalEndpointFilter>()
                .MapPost("/remove", async (HttpContext httpContext, IAntiforgery antiforgery) =>
                {
                    await antiforgery.ValidateRequestAsync(httpContext);
                    return Results.Ok();
                }));

        // The TestServer hands an unhandled exception back to the caller.
        await Assert.ThrowsAsync<AntiforgeryValidationException>(() => PostAsync(site, "/orders/remove"));

        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Equal("/orders/remove", entry.Properties[LoggingContract.RequestPath]);
        Assert.Equal(typeof(AntiforgeryRefusalEndpointFilter).FullName, entry.Category);
        Assert.NotEqual(AntiforgeryRefusalLog.UnknownReason, entry.Properties[LoggingContract.RefusalReason]);
    }

    [Fact]
    public async Task The_endpoint_filter_never_sees_a_form_binding_endpoint_the_framework_refused()
    {
        // THE MEASUREMENT behind the filter's stated limit. No catch and no
        // ValidateRequestAsync in the endpoint: binding a form is what makes UseAntiforgery
        // check the request, and the framework answers 400 before any endpoint filter runs.
        // If a later runtime runs the filter first, this fails and the docs want rewording.
        var filterRan = false;

        await using var site = await StartAsync(app =>
            app.MapGroup("/orders")
                .AddEndpointFilter(async (context, next) =>
                {
                    filterRan = true;
                    return await next(context);
                })
                .AddEndpointFilter<AntiforgeryRefusalEndpointFilter>()
                .MapPost("/bound", ([FromForm] string item) => Results.Ok(item)));

        using var response = await PostAsync(site, "/orders/bound", ("item", "book"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(filterRan);
        Assert.Empty(site.Capture.Refusals);
    }

    [Fact]
    public async Task The_documented_middleware_logs_a_form_binding_endpoint_the_framework_refused()
    {
        // The recipe in the README and on AntiforgeryRefusalEndpointFilter, run as written.
        await using var site = await StartAsync(app =>
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Orders");

            app.Use(async (httpContext, next) =>
            {
                if (httpContext.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false } refused)
                {
                    AntiforgeryRefusalLog.Write(logger, httpContext, refused.Error as AntiforgeryValidationException);
                }

                await next(httpContext);
            });

            app.MapPost("/orders/bound", ([FromForm] string item) => Results.Ok(item));
        });

        using var response = await PostAsync(site, "/orders/bound", ("item", "book"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Equal("/orders/bound", entry.Properties[LoggingContract.RequestPath]);
        Assert.NotEqual(AntiforgeryRefusalLog.UnknownReason, entry.Properties[LoggingContract.RefusalReason]);
    }

    [Fact]
    public async Task A_filter_and_a_catch_on_one_endpoint_write_the_event_once()
    {
        var second = true;

        await using var site = await StartAsync(app =>
            app.MapGroup("/orders")
                .AddEndpointFilter<AntiforgeryRefusalEndpointFilter>()
                .MapPost("/both", async (HttpContext httpContext, IAntiforgery antiforgery, ILoggerFactory loggers) =>
                {
                    try
                    {
                        await antiforgery.ValidateRequestAsync(httpContext);
                    }
                    catch (AntiforgeryValidationException exception)
                    {
                        var logger = loggers.CreateLogger("Orders");
                        AntiforgeryRefusalLog.Write(logger, httpContext, exception);
                        second = AntiforgeryRefusalLog.Write(logger, httpContext, exception);
                        throw;
                    }

                    return Results.Ok();
                }));

        await Assert.ThrowsAsync<AntiforgeryValidationException>(() => PostAsync(site, "/orders/both"));

        // The endpoint wrote it, said no the second time, and the filter stayed quiet.
        Assert.False(second);
        Assert.Equal("Orders", Assert.Single(site.Capture.Refusals).Category);
    }

    [Fact]
    public async Task A_request_that_passes_writes_nothing()
    {
        await using var site = await StartAsync(app =>
            app.MapGroup("/orders")
                .AddEndpointFilter<AntiforgeryRefusalEndpointFilter>()
                .MapPost("/open", () => Results.Ok()));

        using var response = await PostAsync(site, "/orders/open");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(site.Capture.Refusals);
    }

    [Fact]
    public void With_no_exception_and_no_services_the_reason_is_unknown_and_the_default_cap_applies()
    {
        var capture = new Capture();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/orders/add";
        context.Request.Headers.UserAgent = new string('a', 700);

        Assert.True(AntiforgeryRefusalLog.Write(capture.CreateLogger("Orders"), context));

        var entry = Assert.Single(capture.Refusals);
        Assert.Equal("unknown", entry.Properties[LoggingContract.RefusalReason]);
        Assert.Equal(AntiforgeryRefusalLogFilter.UnknownReason, AntiforgeryRefusalLog.UnknownReason);
        Assert.Equal(new string('a', 512), entry.Properties[LoggingContract.UserAgent]);
        Assert.Equal(false, entry.Properties[LoggingContract.HasAntiforgeryCookie]);
    }

    [Fact]
    public async Task Null_arguments_are_refused()
    {
        var logger = new Capture().CreateLogger("Orders");

        Assert.Throws<ArgumentNullException>(() => AntiforgeryRefusalLog.Write(null!, new DefaultHttpContext()));
        Assert.Throws<ArgumentNullException>(() => AntiforgeryRefusalLog.Write(logger, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AntiforgeryRefusalLog.WriteAsync(null!, new DefaultHttpContext()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AntiforgeryRefusalLog.WriteAsync(logger, null!));
        Assert.Throws<ArgumentNullException>(() => new AntiforgeryRefusalEndpointFilter(null!));
    }
}
