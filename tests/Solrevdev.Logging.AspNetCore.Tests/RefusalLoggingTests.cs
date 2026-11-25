using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solrevdev.Logging.AspNetCore.Tests.Pages;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// The two refusal filters, run through real Razor Pages on a TestServer.
/// </summary>
/// <remarks>
/// <para>
/// The cases were ported with the filters, where they called each filter by hand with
/// contexts built in the test. Here the framework builds them, so the anti-forgery case proves the thing the
/// filter's shape depends on: the check short-circuits before the handler, and an always-run
/// result filter still sees it.
/// </para>
/// <para>
/// The pages are <c>Pages/Form.cshtml</c> and <c>Pages/Guarded.cshtml</c>, compiled into this
/// assembly. What the form page's handler does is set per test through
/// <see cref="FormScript"/>.
/// </para>
/// </remarks>
public class RefusalLoggingTests
{
    private const string PipelineRefused = "pipeline.request.refused";
    private const string SignInRefused = "auth.signin.refused";
    private const string Typed = "D02 XY45";

    private sealed record Entry(LogLevel Level, string Category, string Message, IReadOnlyDictionary<string, object?> Properties)
    {
        public string? Event => Properties.GetValueOrDefault(LoggingContract.Event) as string;

        public List<string> List(string name) => Assert.IsAssignableFrom<IEnumerable<string>>(Properties[name]).ToList();
    }

    private sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();

        public IEnumerable<Entry> Named(string eventName) => Entries.Where(entry => entry.Event == eventName);

        public IEnumerable<Entry> Refusals => Entries.Where(entry =>
            entry.Event is RefusalEvents.WebFormRefused or RefusalEvents.WebAntiforgeryRefused);

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

                capture.Entries.Enqueue(new Entry(logLevel, category, formatter(state, exception), properties));
            }
        }
    }

    private sealed class Site(WebApplication app, Capture capture, FormScript script) : IAsyncDisposable
    {
        public Capture Capture => capture;

        public FormScript Script => script;

        public IServiceProvider Services => app.Services;

        public HttpClient Client { get; } = app.GetTestClient();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// A host with Razor Pages and whatever <paramref name="configure"/> adds. The refusal
    /// logging is NOT added here: each test says whether it is on.
    /// </summary>
    private static async Task<Site> StartAsync(Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // The compiled pages are found through the application's own assembly.
            ApplicationName = typeof(RefusalLoggingTests).Assembly.GetName().Name,
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();

        var capture = new Capture();
        var script = new FormScript();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);
        builder.Services.AddSingleton(script);
        builder.Services.AddRazorPages();
        configure(builder.Services);

        var app = builder.Build();
        app.MapRazorPages();
        await app.StartAsync(TestContext.Current.CancellationToken);

        return new Site(app, capture, script);
    }

    private static Task<Site> WithRefusalLoggingAsync(Action<IServiceCollection>? more = null) =>
        StartAsync(services =>
        {
            services.AddSolrevdevRequestLogging();
            services.AddSolrevdevRefusalLogging();
            more?.Invoke(services);
        });

    private static FormUrlEncodedContent Form(params (string Name, string Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

    private static Task<HttpResponseMessage> PostAsync(Site site, string path, HttpContent content) =>
        site.Client.PostAsync(path, content, TestContext.Current.CancellationToken);

    /// <summary>The page refuses its own form: a page-wide error on top of whatever binding found.</summary>
    private static IActionResult RefuseWith(FormModel page, string message)
    {
        page.ModelState.AddModelError(string.Empty, message);
        return page.Page();
    }

    [Fact]
    public async Task A_form_the_page_refused_itself_is_a_Warning_with_field_names_and_no_typed_values()
    {
        await using var site = await WithRefusalLoggingAsync();
        site.Script.Run = page => RefuseWith(page, "Enter the quantity of each item.");

        // The typed value does not bind to a number, so model binding quotes it in its message.
        using var response = await PostAsync(site, "/Form?handler=Save", Form(("Input.Quantity", Typed)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(RefusalEvents.WebFormRefused, entry.Event);
        Assert.Equal("/Form", entry.Properties[LoggingContract.PageName]);
        Assert.Equal("Save", entry.Properties[LoggingContract.PageHandler]);
        Assert.Equal(["(form)", "Input.Quantity"], entry.List(LoggingContract.InvalidFields));

        var errors = entry.List(LoggingContract.FormErrors);
        Assert.Contains("Enter the quantity of each item.", errors);
        Assert.Contains("The value '…' is not valid for Quantity.", errors);
        Assert.DoesNotContain(Typed, entry.Message, StringComparison.Ordinal);
        Assert.False(entry.Properties.ContainsKey(LoggingContract.FollowsEvent));
        Assert.False(entry.Properties.ContainsKey(LoggingContract.FollowsRequest));
        Assert.StartsWith("web.form.refused /Form Save redisplayed, ", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_form_redisplayed_after_a_logged_refusal_names_that_event_and_its_request_at_Information()
    {
        // Something else already wrote the Warning. A second one would count one refusal twice.
        await using var site = await WithRefusalLoggingAsync(services => services.AddSolrevdevLoggedRefusal());
        site.Script.Run = page =>
        {
            page.HttpContext.RequestServices.GetRequiredService<LoggedRefusal>()
                .Logged(PipelineRefused, "ReserveOrderNumberCommand");
            return RefuseWith(page, "That order number is taken.");
        };

        using var response = await PostAsync(site, "/Form?handler=Save", Form(("Input.Quantity", "1")));

        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(RefusalEvents.WebFormRefused, entry.Event);
        Assert.Equal(PipelineRefused, entry.Properties[LoggingContract.FollowsEvent]);
        Assert.Equal("ReserveOrderNumberCommand", entry.Properties[LoggingContract.FollowsRequest]);
        Assert.StartsWith(
            "web.form.refused /Form Save redisplayed after pipeline.request.refused ReserveOrderNumberCommand, ",
            entry.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_form_redisplayed_after_a_refusal_with_no_request_names_the_event_alone()
    {
        // A refused sign-in logs its own Warning and sends no request. The filter reads only
        // that a refusal was logged, and knows nothing of sign-in.
        await using var site = await WithRefusalLoggingAsync(services => services.AddSolrevdevLoggedRefusal());
        site.Script.Run = page =>
        {
            page.HttpContext.RequestServices.GetRequiredService<LoggedRefusal>().Logged(SignInRefused);
            return RefuseWith(page, "The email or password is wrong.");
        };

        using var response = await PostAsync(site, "/Form", Form(("Input.Quantity", "1")));

        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(SignInRefused, entry.Properties[LoggingContract.FollowsEvent]);
        Assert.False(entry.Properties.ContainsKey(LoggingContract.FollowsRequest));

        // An unnamed handler used to print as nothing, and so as a double space.
        Assert.Equal("(default)", entry.Properties[LoggingContract.PageHandler]);
        Assert.Equal(FormRefusalLogFilter.DefaultHandler, entry.Properties[LoggingContract.PageHandler]);
        Assert.DoesNotContain("  ", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_message_the_log_was_given_other_text_for_is_swapped_in_the_form_errors_too()
    {
        // The page copies the refusal's message into its form errors. Where the first line
        // logged fixed text in its place, this line must not put the original back.
        const string shown = "The order cannot be sent, because Jane Doe's account is closed.";
        const string logged = "The order cannot be sent, because the chosen account is closed.";

        await using var site = await WithRefusalLoggingAsync(services => services.AddSolrevdevLoggedRefusal());
        site.Script.Run = page =>
        {
            page.HttpContext.RequestServices.GetRequiredService<LoggedRefusal>()
                .Logged(PipelineRefused, "SendOrderCommand", shownMessage: shown, loggedMessage: logged);
            return RefuseWith(page, shown);
        };

        using var response = await PostAsync(site, "/Form?handler=Save", Form(("Input.Quantity", "1")));

        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Contains(logged, entry.List(LoggingContract.FormErrors));
        Assert.DoesNotContain("Doe", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registered_context_nothing_wrote_to_leaves_the_form_line_a_Warning()
    {
        await using var site = await WithRefusalLoggingAsync(services => services.AddSolrevdevLoggedRefusal());
        site.Script.Run = page => RefuseWith(page, "Enter the quantity of each item.");

        using var response = await PostAsync(site, "/Form", Form(("Input.Quantity", "1")));

        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.False(entry.Properties.ContainsKey(LoggingContract.FollowsEvent));
    }

    private sealed class AppRefusalState
    {
        public string? LastEvent { get; set; }
    }

    private sealed class AppRefusalAdapter(AppRefusalState state) : ILoggedRefusal
    {
        public string? LoggedEvent => state.LastEvent;

        public string? LoggedRequestName => null;

        public string? ShownMessage => null;

        public string? LoggedMessage => null;
    }

    [Fact]
    public async Task An_applications_own_adapter_is_read_through_the_interface()
    {
        // An application whose refusal state lives in a project that cannot reference this
        // package registers a scoped adapter and not the default.
        await using var site = await WithRefusalLoggingAsync(services =>
        {
            services.AddScoped<AppRefusalState>();
            services.AddScoped<ILoggedRefusal, AppRefusalAdapter>();
        });
        site.Script.Run = page =>
        {
            page.HttpContext.RequestServices.GetRequiredService<AppRefusalState>().LastEvent = PipelineRefused;
            return RefuseWith(page, "That order number is taken.");
        };

        using var response = await PostAsync(site, "/Form", Form(("Input.Quantity", "1")));

        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(PipelineRefused, entry.Properties[LoggingContract.FollowsEvent]);
    }

    [Fact]
    public async Task Every_posted_value_of_three_or_more_characters_is_cut_out_and_shorter_ones_are_left()
    {
        await using var site = await WithRefusalLoggingAsync();
        site.Script.Run = page => RefuseWith(
            page,
            $"We could not deliver to {page.Input.PostalCode} for someone@example.com, quantity 12.");

        using var response = await PostAsync(
            site,
            "/Form",
            Form(("Input.Quantity", "12"), ("Input.PostalCode", "  d02 xy45 "), ("Email", "Someone@Example.com")));

        var entry = Assert.Single(site.Capture.Refusals);

        // Trimmed before matching and matched without regard to case. "12" is two characters,
        // so it stays: cutting every short value out of a sentence leaves nothing to read.
        // The page echoed the value as posted, spaces and all, so those spaces remain.
        Assert.Equal(
            ["We could not deliver to   …  for …, quantity 12."],
            entry.List(LoggingContract.FormErrors));
    }

    [Fact]
    public async Task Messages_are_capped_at_300_characters_each_and_20_in_all()
    {
        await using var site = await WithRefusalLoggingAsync();
        site.Script.Run = page =>
        {
            page.ModelState.AddModelError(string.Empty, new string('x', 400));
            for (var index = 0; index < 30; index++)
            {
                page.ModelState.AddModelError(string.Empty, $"Line {index:00} is wrong.");
            }

            return page.Page();
        };

        using var response = await PostAsync(site, "/Form", Form(("Input.Quantity", "1")));

        var errors = Assert.Single(site.Capture.Refusals).List(LoggingContract.FormErrors);
        Assert.Equal(20, errors.Count);
        Assert.Equal(new string('x', 300) + "…", errors[0]);
    }

    [Fact]
    public async Task A_valid_post_a_redirect_and_a_get_write_no_form_line()
    {
        await using var site = await WithRefusalLoggingAsync();

        site.Script.Run = page => page.Page();
        using var valid = await PostAsync(site, "/Form", Form(("Input.Quantity", "1")));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        // A page that keeps its message elsewhere and redirects: the first line is its record.
        site.Script.Run = page =>
        {
            page.ModelState.AddModelError(string.Empty, "Refused, and redirected.");
            return page.RedirectToPage("/Form");
        };
        using var redirected = await PostAsync(site, "/Form", Form(("Input.Quantity", "1")));
        Assert.Equal(HttpStatusCode.Redirect, redirected.StatusCode);

        using var get = await site.Client.GetAsync("/Form", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        Assert.Empty(site.Capture.Refusals);
    }

    [Fact]
    public async Task A_post_with_no_anti_forgery_token_is_logged_with_the_reason_the_cookie_flag_and_a_cut_user_agent()
    {
        await using var site = await StartAsync(services =>
        {
            services.AddSolrevdevRequestLogging(options => options.MaximumUserAgentLength = 20);
            services.AddSolrevdevRefusalLogging();
        });
        var longAgent = "TestBrowser/1.0 " + new string('a', 700);
        site.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", longAgent);

        using var response = await PostAsync(site, "/Guarded", Form(("Email", "nobody@example.com")));

        // The check answers 400 before the handler runs. The filter still saw it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(RefusalEvents.WebAntiforgeryRefused, entry.Event);
        Assert.Equal("POST", entry.Properties[LoggingContract.RequestMethod]);
        Assert.Equal("/Guarded", entry.Properties[LoggingContract.RequestPath]);
        Assert.Equal(false, entry.Properties[LoggingContract.HasAntiforgeryCookie]);

        // The framework's own words, which name what was missing and never a token.
        var reason = Assert.IsType<string>(entry.Properties[LoggingContract.RefusalReason]);
        Assert.NotEqual(AntiforgeryRefusalLogFilter.UnknownReason, reason);
        Assert.Contains("antiforgery", reason, StringComparison.OrdinalIgnoreCase);

        // Cut to the application's configured cap, as the request summary cuts it.
        Assert.Equal(longAgent[..20], entry.Properties[LoggingContract.UserAgent]);
        Assert.DoesNotContain("nobody@example.com", entry.Message, StringComparison.Ordinal);
        Assert.StartsWith("web.antiforgery.refused POST /Guarded refused: ", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_user_agent_is_cut_to_the_package_default_without_the_request_logging_registration()
    {
        await using var site = await StartAsync(services => services.AddSolrevdevRefusalLogging());
        var longAgent = "TestBrowser/1.0 " + new string('a', 700);
        site.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", longAgent);

        using var response = await PostAsync(site, "/Guarded", Form());

        Assert.Equal(longAgent[..512], Assert.Single(site.Capture.Refusals).Properties[LoggingContract.UserAgent]);
    }

    [Fact]
    public async Task A_caller_with_no_user_agent_gets_no_user_agent_property()
    {
        // Absent, never blank.
        await using var site = await WithRefusalLoggingAsync();

        using var response = await PostAsync(site, "/Guarded", Form());

        var entry = Assert.Single(site.Capture.Refusals);
        Assert.False(entry.Properties.ContainsKey(LoggingContract.UserAgent));
        Assert.Equal(false, entry.Properties[LoggingContract.HasAntiforgeryCookie]);
    }

    [Fact]
    public async Task A_caller_that_sent_the_cookie_and_no_field_says_so()
    {
        // The case worth telling apart: a browser with a session whose form lost its token,
        // against a tool that never loaded the page.
        await using var site = await WithRefusalLoggingAsync();
        site.Client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", ".AspNetCore.Antiforgery.abc123=not-a-real-token");

        using var response = await PostAsync(site, "/Guarded", Form());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(true, Assert.Single(site.Capture.Refusals).Properties[LoggingContract.HasAntiforgeryCookie]);
    }

    [Fact]
    public async Task With_the_registration_absent_nothing_is_logged_and_no_filter_is_added()
    {
        // THE NO-CHANGE PROOF for the filters. An application that upgrades and does not call
        // AddSolrevdevRefusalLogging gets the same two responses and neither event.
        await using var site = await StartAsync(services => services.AddSolrevdevRequestLogging());
        site.Script.Run = page => RefuseWith(page, "Enter the quantity of each item.");

        using var form = await PostAsync(site, "/Form?handler=Save", Form(("Input.Quantity", Typed)));
        using var guarded = await PostAsync(site, "/Guarded", Form());

        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, guarded.StatusCode);
        Assert.Empty(site.Capture.Refusals);
        Assert.DoesNotContain(site.Capture.Entries, entry => entry.Category.StartsWith("Solrevdev.", StringComparison.Ordinal));
        Assert.Empty(RegisteredFilters(site.Services));
        Assert.Null(site.Services.CreateScope().ServiceProvider.GetService<ILoggedRefusal>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Each_filter_is_switched_on_its_own(bool antiforgery, bool form)
    {
        await using var site = await StartAsync(services => services.AddSolrevdevRefusalLogging(options =>
        {
            options.LogAntiforgeryRefusals = antiforgery;
            options.LogFormRefusals = form;
        }));
        site.Script.Run = page => RefuseWith(page, "Enter the quantity of each item.");

        using var formResponse = await PostAsync(site, "/Form", Form(("Input.Quantity", "1")));
        using var guardedResponse = await PostAsync(site, "/Guarded", Form());

        Assert.Equal(form ? 1 : 0, site.Capture.Named(RefusalEvents.WebFormRefused).Count());
        Assert.Equal(antiforgery ? 1 : 0, site.Capture.Named(RefusalEvents.WebAntiforgeryRefused).Count());
    }

    [Fact]
    public async Task Both_are_on_by_default_and_calling_twice_adds_each_once()
    {
        await using var site = await StartAsync(services =>
        {
            services.AddSolrevdevRefusalLogging();
            services.AddSolrevdevRefusalLogging();
        });

        Assert.Equal(
            [typeof(AntiforgeryRefusalLogFilter), typeof(FormRefusalLogFilter)],
            RegisteredFilters(site.Services));
    }

    [Fact]
    public async Task The_events_carry_only_contract_names()
    {
        // THE DRIFT TEST for the two events: every property either filter writes is a
        // LoggingContract constant, and the set of new ones is exactly RefusalProperties.
        await using var site = await WithRefusalLoggingAsync(services => services.AddSolrevdevLoggedRefusal());
        site.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TestBrowser/1.0");
        site.Script.Run = page =>
        {
            page.HttpContext.RequestServices.GetRequiredService<LoggedRefusal>().Logged(PipelineRefused, "SaveCommand");
            return RefuseWith(page, "Refused.");
        };

        using var form = await PostAsync(site, "/Form", Form(("Input.Quantity", "1")));
        using var guarded = await PostAsync(site, "/Guarded", Form());

        var written = site.Capture.Refusals
            .SelectMany(entry => entry.Properties.Keys)
            .Where(name => name != "{OriginalFormat}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            LoggingContract.RefusalProperties
                .Concat([LoggingContract.Event, LoggingContract.RequestMethod, LoggingContract.RequestPath, LoggingContract.UserAgent])
                .Order(),
            written.Order());
    }

    private static IEnumerable<string> Names(Entry entry) =>
        entry.Properties.Keys.Where(name => name != "{OriginalFormat}").Order();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_anti_forgery_filter_writes_exactly_its_set_in_each_mode(bool sendsUserAgent)
    {
        // THE DRIFT TEST per event. All of the set with a user agent, the set minus its
        // optional part without one, and never a name outside it.
        await using var site = await WithRefusalLoggingAsync();
        if (sendsUserAgent)
        {
            site.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TestBrowser/1.0");
        }

        using var response = await PostAsync(site, "/Guarded", Form());

        var expected = sendsUserAgent
            ? LoggingContract.AntiforgeryRefusalProperties
            : LoggingContract.AntiforgeryRefusalProperties.Except(LoggingContract.AntiforgeryRefusalOptionalProperties);

        Assert.Equal(expected.Order(), Names(Assert.Single(site.Capture.Refusals)));
    }

    [Theory]
    [InlineData(false, false, LogLevel.Warning)]
    [InlineData(true, false, LogLevel.Information)]
    [InlineData(true, true, LogLevel.Information)]
    public async Task The_form_filter_writes_exactly_its_set_in_each_mode(bool follows, bool hasRequest, LogLevel level)
    {
        await using var site = await WithRefusalLoggingAsync(services => services.AddSolrevdevLoggedRefusal());
        site.Script.Run = page =>
        {
            if (follows)
            {
                page.HttpContext.RequestServices.GetRequiredService<LoggedRefusal>()
                    .Logged(PipelineRefused, hasRequest ? "SaveCommand" : null);
            }

            return RefuseWith(page, "Refused.");
        };

        using var response = await PostAsync(site, "/Form", Form(("Input.Quantity", "1")));

        var always = LoggingContract.FormRefusalProperties.Except(LoggingContract.FormRefusalOptionalProperties);
        var expected = always
            .Concat(follows ? [LoggingContract.FollowsEvent] : [])
            .Concat(hasRequest ? [LoggingContract.FollowsRequest] : []);

        var entry = Assert.Single(site.Capture.Refusals);
        Assert.Equal(level, entry.Level);
        Assert.Equal(expected.Order(), Names(entry));
    }

    [Fact]
    public void The_per_event_sets_are_exact_and_the_union_is_what_they_added()
    {
        Assert.Equal(
            new[] { "Event", "HasAntiforgeryCookie", "RefusalReason", "RequestMethod", "RequestPath", "UserAgent" },
            LoggingContract.AntiforgeryRefusalProperties.Order());
        Assert.Equal(["UserAgent"], LoggingContract.AntiforgeryRefusalOptionalProperties);

        Assert.Equal(
            new[] { "Event", "FollowsEvent", "FollowsRequest", "FormErrors", "InvalidFields", "PageHandler", "PageName" },
            LoggingContract.FormRefusalProperties.Order());
        Assert.Equal(
            new[] { "FollowsEvent", "FollowsRequest" },
            LoggingContract.FormRefusalOptionalProperties.Order());

        Assert.Subset(
            LoggingContract.AntiforgeryRefusalProperties.ToHashSet(),
            LoggingContract.AntiforgeryRefusalOptionalProperties.ToHashSet());
        Assert.Subset(
            LoggingContract.FormRefusalProperties.ToHashSet(),
            LoggingContract.FormRefusalOptionalProperties.ToHashSet());

        // RefusalProperties is the two sets together, less the names older sets already had.
        Assert.Equal(
            LoggingContract.AntiforgeryRefusalProperties
                .Concat(LoggingContract.FormRefusalProperties)
                .Except(LoggingContract.AllProperties)
                .Except(LoggingContract.ExtendedProperties)
                .Order(),
            LoggingContract.RefusalProperties.Order());
    }

    [Fact]
    public void The_refusal_set_is_exactly_the_eight_names_and_in_no_other_set()
    {
        Assert.Equal(
            new[]
            {
                "FollowsEvent", "FollowsRequest", "FormErrors", "HasAntiforgeryCookie",
                "InvalidFields", "PageHandler", "PageName", "RefusalReason"
            },
            LoggingContract.RefusalProperties.Order());

        Assert.Empty(LoggingContract.RefusalProperties.Intersect(LoggingContract.AllProperties));
        Assert.Empty(LoggingContract.RefusalProperties.Intersect(LoggingContract.OptionalProperties));
        Assert.Empty(LoggingContract.RefusalProperties.Intersect(LoggingContract.OptInProperties));
        Assert.Empty(LoggingContract.RefusalProperties.Intersect(LoggingContract.ExtendedProperties));
    }

    [Fact]
    public void The_two_event_names_are_fixed_and_follow_the_convention()
    {
        Assert.Equal("web.antiforgery.refused", RefusalEvents.WebAntiforgeryRefused);
        Assert.Equal("web.form.refused", RefusalEvents.WebFormRefused);
        Assert.Empty(EventNames.Violations(typeof(RefusalEvents)));
    }

    [Fact]
    public void The_default_context_starts_empty_and_refuses_a_blank_event_name()
    {
        var refusal = new LoggedRefusal();

        Assert.Null(refusal.LoggedEvent);
        Assert.Null(refusal.LoggedRequestName);
        Assert.ThrowsAny<ArgumentException>(() => refusal.Logged(" "));

        refusal.Logged(PipelineRefused, "SaveCommand", "shown", "logged");
        ILoggedRefusal read = refusal;
        Assert.Equal(
            (PipelineRefused, "SaveCommand", "shown", "logged"),
            (read.LoggedEvent, read.LoggedRequestName, read.ShownMessage, read.LoggedMessage));
    }

    [Fact]
    public void The_default_context_does_not_replace_an_adapter_already_registered()
    {
        var services = new ServiceCollection();
        services.AddScoped<AppRefusalState>();
        services.AddScoped<ILoggedRefusal, AppRefusalAdapter>();
        services.AddSolrevdevLoggedRefusal();

        using var scope = services.BuildServiceProvider().CreateScope();

        Assert.IsType<AppRefusalAdapter>(scope.ServiceProvider.GetRequiredService<ILoggedRefusal>());
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => RefusalLoggingExtensions.AddSolrevdevRefusalLogging(null!));
        Assert.Throws<ArgumentNullException>(() => RefusalLoggingExtensions.AddSolrevdevLoggedRefusal(null!));
        Assert.Throws<ArgumentNullException>(() => new FormRefusalLogFilter(null!));
        Assert.Throws<ArgumentNullException>(() => new AntiforgeryRefusalLogFilter(null!, Options.Create(new RequestLogOptions())));
    }

    private static List<Type> RegisteredFilters(IServiceProvider services) =>
        services.GetRequiredService<IOptions<MvcOptions>>().Value.Filters
            .OfType<TypeFilterAttribute>()
            .Select(filter => filter.ImplementationType)
            .Where(type => type.Assembly == typeof(LoggingContract).Assembly)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();
}
