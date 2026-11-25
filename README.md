<div align="center">
  <img src="docs/assets/branding/logging-icon.svg" alt="solrevdev.Logging.AspNetCore" width="128" height="128">
  <h1>solrevdev.logging</h1>
  <p><strong>Shared Serilog request logging for ASP.NET Core.</strong> One set of property names, one request summary and one set of rules about what is logged, so several applications writing into one Seq mean the same thing by the same query.</p>
  <p>
    <a href="https://www.nuget.org/packages/solrevdev.Logging.AspNetCore/"><img src="https://img.shields.io/nuget/v/solrevdev.Logging.AspNetCore.svg?logo=nuget" alt="NuGet"></a>
    <a href="https://www.nuget.org/packages/solrevdev.Logging.AspNetCore/"><img src="https://img.shields.io/nuget/dt/solrevdev.Logging.AspNetCore.svg" alt="NuGet downloads"></a>
    <a href="https://github.com/solrevdev/solrevdev.logging/actions/workflows/ci.yml"><img src="https://github.com/solrevdev/solrevdev.logging/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
    <img src="https://img.shields.io/badge/.NET-10%20%7C%2011-512BD4?logo=dotnet&logoColor=white" alt=".NET 10 | 11">
    <img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT">
  </p>
</div>

---

## Contents

- [Why this exists](#why-this-exists)
- [Install](#install)
- [Upgrading from 1.2.0](#upgrading-from-120)
- [Quick start](#quick-start)
- [Set up Serilog](#set-up-serilog)
- [Enable forwarded headers](#enable-forwarded-headers)
- [Wire up the middleware](#wire-up-the-middleware)
- [What gets logged](#what-gets-logged)
- [Opt-in properties](#opt-in-properties)
- [Your own summary fields](#your-own-summary-fields)
- [Startup and failure helpers](#startup-and-failure-helpers)
- [Refusal events](#refusal-events)
- [Event names](#event-names)
- [OpenTelemetry names](#opentelemetry-names)
- [Client addresses behind a load balancer](#client-addresses-behind-a-load-balancer)
- [The traps this package holds](#the-traps-this-package-holds)
- [What this package must never grow](#what-this-package-must-never-grow)
- [Options](#options)
- [Testing your application against the contract](#testing-your-application-against-the-contract)
- [Build and test](#build-and-test)
- [Maintainers](#maintainers)
- [Licence](#licence)

## Why this exists

Several applications write into one Seq server and are told apart by an `Application`
property. This package gives them the same property names, the same request summary and the
same rules about what is logged and what is left out, so one query means the same thing
across all of them.

- **The property names are fixed, on purpose.** A property that means the same thing under two
  names is a filter that returns half the traffic and looks like a quiet day. `LoggingContract`
  owns the names and your application can test against them.
- **An address a person can read.** A dual-stack socket delivers an IPv4 caller as
  `::ffff:203.0.113.42` and a local one as `::1`. Both come out dotted, without running a
  genuine IPv6 address through `MapToIPv4`, which returns a wrong but plausible answer instead
  of throwing.
- **A referer with its query string gone.** Password-reset and confirmation tokens ride in
  query strings and browsers send the previous page as the next request's Referer. That is a
  security control here, and it is unit-tested as one.
- **Context on both sides of the rate limiter.** A refused authentication POST short-circuits
  below the limiter, and the two-middleware split is what keeps its 429 from logging with no
  address and no trace id.
- **Quiet traffic demoted, not dropped.** Successful static files, `/healthcheck` and
  `/version` log at Verbose. The same paths failing still log at their real level.
- **Properties are absent rather than blank.** "Missing" and "empty" are not the same row in
  the log store, so a query can ask about absence.
- **Wiring mistakes fail at startup.** Forget `AddSolrevdevRequestLogging` and the `Use` calls
  throw, instead of the application running on package defaults it never chose.

## Install

```bash
dotnet add package solrevdev.Logging.AspNetCore
```

Targets `net10.0` and `net11.0`. Depends on `Serilog.AspNetCore` 10.0.0 and the ASP.NET Core shared
framework.

## Upgrading from 1.2.0

No existing API changed and no existing output changed. An application that bumps the version
and touches nothing else logs the same properties with the same values, and its contract
tests pass unchanged. Every new property and both filters are off until you switch them on.

**The upgrade can still fail to compile.** 1.3.0 adds nineteen public type names, all in the
`Solrevdev.Logging.AspNetCore` namespace. If your application has a type of its own with one
of these names, in a file that imports that namespace, the compiler reports CS0104, ambiguous
reference. That is the likely case for a type this package was ported from: one application
got it on five types from the version bump alone. Delete your copy and use the package's, or
keep yours for now behind an alias:

```csharp
using SelfLogOutput = MyApp.Infrastructure.SelfLogOutput;
```

Search your source for these before upgrading:

`AntiforgeryRefusalEndpointFilter`, `AntiforgeryRefusalLog`, `AntiforgeryRefusalLogFilter`,
`BootstrapHost`, `BootstrapLogIdentity`, `EventNameViolation`, `EventNameViolationKind`,
`EventNames`, `FormRefusalLogFilter`, `HeaderValue`, `ILoggedRefusal`, `LoggedRefusal`,
`RefusalEvents`, `RefusalLogOptions`, `RefusalLoggingExtensions`, `RequestNavigation`,
`RequestQuery`, `SelfLogOutput`, `TransportExceptionLog`.

`HeaderValue` existed in 1.2.0 as an internal type and is now public, so it is in the list.

## Quick start

```csharp
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Your logger. Enrich.FromLogContext() is required by this package.
builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "orders")
    .WriteTo.Console());

// This package. Required: each Use call below throws at startup without it.
builder.Services.AddSolrevdevRequestLogging();

var app = builder.Build();

// The order is part of the API. Every wrong position fails quietly.
app.UseForwardedHeaders();          // yours to configure; see below
app.UseSolrevdevClientLogContext(); // ClientIp and TraceId
app.UseSolrevdevRequestLogging();   // Serilog's request summary
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseSolrevdevUserLogContext();   // UserId and UserEmail
app.UseAuthorization();
```

Two things that snippet does not do for you, both of which change what you get:
`AddSolrevdevRequestLogging` configures neither Serilog nor forwarded headers. The next two
sections cover each, and the rest of the file explains what is logged and the measured
traps behind it.

## Set up Serilog

This package does not configure Serilog. Your application owns its logger, its sinks and its
enrichers. Two of those choices matter to what follows.

`Enrich.FromLogContext()` is required. The client and user middleware push their properties
onto Serilog's `LogContext`, and without that enricher the properties never reach your
ordinary application log events. The request summary is enriched separately and still works,
so the failure looks like half the properties turning up.

```csharp
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "orders")
    .WriteTo.Console());
```

Add your own sink, usually Seq, alongside the console. `Application` can come from
`Serilog:Properties` in configuration instead of the call above.

## Enable forwarded headers

Behind a load balancer, the address the application sees on the connection is the balancer's
until forwarded-header processing is enabled and configured. `app.UseForwardedHeaders()` on
its own does not do it: the default `ForwardedHeadersOptions.ForwardedHeaders` is `None`, so
nothing is read.

Which peers to trust is your application's decision and this package will never make it. The
shape below is for one AWS Application Load Balancer with a single hop to the application.
Check your own network path before copying it: an NLB, CloudFront in front of an ALB, or nginx
in front of either is a different number of hops and a different trust list.

```csharp
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    // The defaults trust loopback only, which is not where a balancer is. Clear them and
    // state the peers or networks this deployment actually has in front of it.
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("203.0.113.0"), 24));
});
```

Two details worth knowing before you paste that.

`KnownIPNetworks` is the .NET 8 replacement for `KnownNetworks`, which is obsolete and warns
on .NET 10. It holds `System.Net.IPNetwork`, and `Microsoft.AspNetCore.HttpOverrides` has a
type of the same name, so with both namespaces in scope the name has to be qualified or the
file will not compile.

`ForwardLimit` is a cap on how many entries are consumed, counting from the right. It should
match the number of trusted hops in front of the application. A limit higher than the real
topology lets a caller's own entry be taken as the client address.

The example addresses here are from TEST-NET-3. Substitute your own; do not ship
`203.0.113.0/24` as a trust list.

## Wire up the middleware

```csharp
builder.Services.AddSolrevdevRequestLogging();

app.UseForwardedHeaders();
app.UseSolrevdevClientLogContext(); // ClientIp and TraceId
app.UseSolrevdevRequestLogging();   // Serilog's request summary
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseSolrevdevUserLogContext();   // UserId and UserEmail
app.UseAuthorization();
```

`AddSolrevdevRequestLogging` registers the options, validates them and records that the package
was added. It does not configure Serilog, forwarded headers, authentication or rate limiting.
The static-file, rate-limiter and authentication lines above assume your application already
uses and configures those features.

The call is required. All three `Use` calls throw `InvalidOperationException` at startup
without it:

```text
Unable to find the required services. Please add all the required services by calling
'IServiceCollection.AddSolrevdevRequestLogging' in the application startup code.
```

That check exists because the options cannot provide one. `IOptions<RequestLogOptions>`
resolves to a default instance in any ASP.NET Core host, registered or not, so an application
that forgot the call used to start and log every request with package defaults it never chose.

The order is part of the API. Pin it with a test in your own application, because every wrong
position fails quietly rather than loudly.

| Move | What breaks |
| --- | --- |
| Client log context below the rate limiter | A refused authentication POST logs its 429 with no address and no trace id. That is the event somebody is most likely to go looking for |
| Client log context above `UseForwardedHeaders` | Every request logs the balancer's address |
| User log context above `UseAuthentication` | There is no principal to read, so nothing is set |
| User log context below `UseAuthorization` | An authorization refusal no longer names who was refused |
| Request logging below `UseStaticFiles` | Nothing breaks, but static files stop being demoted to Verbose |

Exception handling and HTTPS redirection go below the client context. Request logging goes
above the components whose responses you want it to record. Do not merge the two context
middleware: the user one can only sit between authentication and authorization, and that is
below anything that short-circuits a request.

## What gets logged

| Property | Where from |
| --- | --- |
| `ClientIp` | `Connection.RemoteIpAddress` after forwarded headers, rendered by `ClientAddress.Describe` |
| `TraceId` | `Activity.Current`, as an ordinary property |
| `ForwardedFor` | A best-effort reconstruction of the caller's `X-Forwarded-For`. Untrusted and lossy |
| `Referer` | Scheme, host and path. The query string and fragment are removed |
| `UserAgent` | Capped, 512 characters by default |
| `RequestHost`, `RequestScheme` | The request, after forwarded headers |
| `UserId`, `UserEmail` | The principal, only when authenticated |

`Application`, `Environment`, `MachineName`, `ProcessId` and `ThreadId` come from your
enrichers. `RequestMethod`, `RequestPath`, `StatusCode` and `Elapsed` come from Serilog's
request logging. `LoggingContract` names all eighteen and splits them by which component sets
them. Three more are off until you ask for them: see [Opt-in properties](#opt-in-properties).

Serilog carries a trace id of its own as a built-in field on the event rather than as a
property. `TraceId` records the same id as an ordinary property, so it appears in output
templates and queries beside everything else, and a queued row can be matched back to the
request that queued it.

Successful static-file, `/healthcheck` and `/version` requests are logged at Verbose. Most
other requests are Information. Server errors and unhandled exceptions are Error. A quiet path
returning 4xx is still logged at Information. Verbose events only survive if your logger and
sink levels allow them.

### Absent, not empty

Nothing here writes an empty string to keep a shape. A request with no Referer header carries
no `Referer` property. An anonymous request carries neither user property. An authenticated
user with no email claim carries no `UserEmail`. A claim holding an empty string counts as
absent, in the log context and in the request summary alike.

The convention exists so that "the property is missing" and "the value was blank" are not the
same row in the log store. Query for absence rather than for an empty value.

## Opt-in properties

Three properties are off until you switch each one on, so upgrading from 1.2.0 changes
nothing you log. With more than one on, they are set in the order `SpanId`, `Navigation`,
`RequestQuery`, after the package's other properties and before your own enrichers. That is
the order of properties on the event and nothing depends on it.

```csharp
builder.Services.AddSolrevdevRequestLogging(options =>
{
    options.IncludeNavigation = true;
    options.IncludeRequestQuery = true;
    options.IncludeSpanId = true;

    // Keys whose values may be logged. Empty by default.
    options.RequestQueryKeys.Add("handler");
    options.RequestQueryKeys.Add("pageNumber");
    options.RequestQueryKeys.Add("orderId");
});
```

| Property | Option | What it is |
| --- | --- | --- |
| `Navigation` | `IncludeNavigation` | How the browser says the request was started, from the `Sec-Fetch-*` headers |
| `RequestQuery` | `IncludeRequestQuery` | The query string, cut down to an allowlist |
| `SpanId` | `IncludeSpanId` | The request span's id from `Activity.Current`, beside `TraceId` |

### Navigation

`Referer` says where a request came from when one was sent. It cannot say a request was not a
click. `Navigation` is one word, decided in this order:

| Value | Meaning |
| --- | --- |
| `fetch` | Not a page load, but a script on the page asking |
| `frame` | A page loaded inside another one |
| `direct` | A page opened with no page behind it: typed, pasted, a bookmark, or a browser driven by a tool |
| `cross-site` | A link or form on another site |
| `user` | A click, key press or form submit on one of this site's pages, and the redirect that follows it |
| `scripted` | A page load from this site that no click started |

It is absent when the caller sent none of the headers, which is what curl, a test client and
an older browser do. The headers are the browser's word, so a tool can send any of them. The
values are constants on `RequestNavigation`, and `RequestNavigation.Describe(headers)` is
public.

### RequestQuery

`RequestPath` has no query string, so two requests for one page that differ only in the query
write the same line. `RequestQuery` adds the query without the caller's text:

| Query | Logged |
| --- | --- |
| A key in `RequestQueryKeys` with a value that looks like an id, a number, a date or an option name | `key=value` |
| Any other key, or a listed key with any other value | `key=(set)` |
| An empty value, or a key with no value | `key=` |
| A key that is not a plain name | `(other)=(set)` |

A loggable value is 1 to `MaximumRequestQueryValueLength` ASCII letters, digits, `_`, `.`, `:`
or `-`. No space, no `@` and no slash, so no address, email or URL fits. Keys are sorted,
repeated pairs are written once, and the first `MaximumRequestQueryPairs` are kept, followed by
`&…` when more were cut.

`RequestQueryKeys` starts empty, so switching the property on before listing a key shows which
keys were sent and no value at all. List the keys that name a record, a page or a filter.
Leave out anything a person types, such as a search box or a return URL.

The property is absent when the request has no query, and when it matched no endpoint.
Static files are served before routing, so they never pay for the work.
`RequestQuery.Describe` is public.

The engine was ported from an application that had run it in production. When that
application adopted the package, a fuzz of 10,011 queries through both engines gave the same
output for every one. Two cases outside that fuzz differ, on purpose. The original
matched with regular expressions ending in `$`, which also matches just before a final line
feed:

| Input | Original | This package |
| --- | --- | --- |
| A listed key whose VALUE ends in a line feed | Logged, line feed and all | `key=(set)` |
| A KEY that ends in a line feed | Written by name, line feed and all | `(other)` |

Neither a value nor a key that carries a line feed is a plain name, so both are withheld.

### SpanId

Serilog stamps a span id on every event as a built-in field, which Seq reads. A text or
`.clef` line cannot show it by name. `SpanId` is the request span's id as an ordinary
property, on the request summary and on every event inside the request, so a line in a file
matches a span without Seq. It is the request's span, not a child span opened further in.

## Your own summary fields

Add them to `SummaryEnrichers`. They run after the package's own, in the order added.

```csharp
builder.Services.AddSolrevdevRequestLogging(options =>
{
    options.SummaryEnrichers.Add((diagnosticContext, httpContext) =>
        diagnosticContext.Set("Region", httpContext.GetRouteValue("region")));
});

app.UseSolrevdevRequestLogging();
```

Before 1.3.0 the only way to add a field was to call Serilog's `UseSerilogRequestLogging`
yourself and wire `RequestSummaryEnricher` and `RequestLogLevel` by hand, which skips the
startup guard. That still works. The list is the way to stop doing it.

Once the options are registered, `app.UseSolrevdevRequestLogging()` with no arguments is the
whole wiring: the package's enricher, your enrichers, the opt-in properties and the level
function. So a test that pinned the text of a hand-wired `UseSerilogRequestLogging` block in
`Program.cs` can go. Assert on the options the built host resolved instead, which survives
the line being moved or reworded:

```csharp
var options = factory.Services.GetRequiredService<IOptions<RequestLogOptions>>().Value;

Assert.True(options.IncludeRequestQuery);
Assert.Contains("orderId", options.RequestQueryKeys);
Assert.Single(options.SummaryEnrichers);
```

**An enricher that throws is caught.** The exception goes to Serilog's `SelfLog`, the next
enricher still runs, and the package's properties are already on the summary because they
were set first. Letting it throw is worse than it sounds. Measured against Serilog.AspNetCore
10.0.0: the request fails with the enricher's exception after the page has run, and no
summary line is written for it at all. Enable `SelfLog` or you will not hear about a broken
enricher.

Do not set a `LoggingContract` name from an enricher. The last write wins, so it would
replace the package's value.

`UseSolrevdevRequestLogging` reads the options you registered from the application's own
service provider. It does not depend on `HttpContext.RequestServices`, so the package-default
fallback described under
[A bare DefaultHttpContext gets package defaults](#a-bare-defaulthttpcontext-gets-package-defaults)
applies only to the two short overloads called directly.

## Startup and failure helpers

Four small pieces every application had copied or lacked. None is wired for you.

```csharp
// First line in the process, before any logger exists.
SelfLog.Enable(new SelfLogOutput(Console.Error, "See the logging runbook.").Write);

// Configuration may fail to load, so name the bootstrap logger without reading a file.
var identity = BootstrapLogIdentity.Resolve(args, defaultApplication: "my-app");
Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty(LoggingContract.Application, identity.Application)
    .Enrich.WithProperty(LoggingContract.Environment, identity.EnvironmentName)
    .WriteTo.Console()
    .CreateBootstrapLogger();
```

**`SelfLogOutput`** prints Serilog's SelfLog, with one change. The first time the Seq sink
cannot connect it writes one line. Later connection failures are not shown. Everything else,
including a Seq that answers and refuses the events, is printed whole. The second argument is
an optional closing sentence for that one line. The third is an optional prefix for the
first line of every message it writes, so one search of deploy output finds them all:
`new SelfLogOutput(Console.Error, prefix: "Serilog self log:")`. Known gap: the line is written once per
process, so a Seq that comes back and stops again is not reported a second time.

**`BootstrapLogIdentity.Resolve`** reads launch arguments and environment variables only.
`Application` is the `Serilog:Properties:Application` argument, then the
`Serilog__Properties__Application` variable, then the default you pass. When an argument is
repeated the last one wins.

`EnvironmentName` has to match what your host settles on, or the first events of a process
land under one `Environment` and the rest under another. The hosts do not agree with each
other, so say which one you build. Measured on .NET 10.0.15 and 11.0.0, by building each host
in a test and comparing:

| `BootstrapHost` | Your `Program.cs` builds | Order, first one set wins |
| --- | --- | --- |
| `WebApplication` (default) | `WebApplication.CreateBuilder` or `CreateSlimBuilder` | `--environment`, `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` |
| `WebHostDefaults` | `Host.CreateDefaultBuilder().ConfigureWebHostDefaults`, the `Startup` shape | `ASPNETCORE_ENVIRONMENT`, `--environment`, `DOTNET_ENVIRONMENT` |
| `Worker` | `Host.CreateApplicationBuilder`, or `Host.CreateDefaultBuilder` with no web host | `--environment`, `DOTNET_ENVIRONMENT` |

All three end at `Production`. Two rows are not what is usually said. For
`WebApplication.CreateBuilder`, `DOTNET_ENVIRONMENT` beats `ASPNETCORE_ENVIRONMENT` when both
are set. For `ConfigureWebHostDefaults`, `ASPNETCORE_ENVIRONMENT` beats even the
`--environment` argument. A worker never reads `ASPNETCORE_ENVIRONMENT`.

```csharp
var identity = BootstrapLogIdentity.Resolve(args, "my-worker", host: BootstrapHost.Worker);
```

**`TransportExceptionLog.Snapshot`** stands in for an exception that is, or wraps, an
`HttpRequestException`. The stand-in keeps the outer type, the HTTP status and the stack
frames. It drops every message in the chain, because those can carry the remote service's
response text and the request URL. Any other exception comes back untouched.

```csharp
catch (Exception exception)
{
    logger.LogError(TransportExceptionLog.Snapshot(exception), "{Event} payment failed", MyEvents.PaymentFailed);
}
```

**`HeaderValue.Truncate`** is the cut the request summary uses, now public. An application
that writes `UserAgent` on an event of its own passes its configured cap, so one name never
carries two lengths:

```csharp
var userAgent = HeaderValue.Truncate(
    Request.Headers.UserAgent.ToString(),
    requestLogOptions.Value.MaximumUserAgentLength);
```

It returns null for a blank value, so set the property only when there is one.

## Refusal events

Two MVC filters log the two refusals a page otherwise answers without a word. Both are
opt-in: an application that does not call `AddSolrevdevRefusalLogging` logs what it logged
before.

```csharp
builder.Services.AddSolrevdevRequestLogging();
builder.Services.AddSolrevdevRefusalLogging();   // both filters, as global MVC filters
builder.Services.AddSolrevdevLoggedRefusal();    // optional: the default ILoggedRefusal
```

To add one and not the other:

```csharp
builder.Services.AddSolrevdevRefusalLogging(options => options.LogFormRefusals = false);
```

**`web.antiforgery.refused`** is a form post that failed its anti-forgery check. The
framework's own line is Information under `Microsoft.AspNetCore`, which is usually floored at
Warning, so the person gets a bare 400 and the log says nothing. `AntiforgeryRefusalLogFilter`
is an always-run result filter, because the check short-circuits before any page handler. It
validates again to read the reason, which names the missing cookie, header or field and never
a token.

| Property | Value |
| --- | --- |
| `Event` | `web.antiforgery.refused` |
| `RequestMethod`, `RequestPath` | The request |
| `RefusalReason` | The framework's message, or `unknown` |
| `HasAntiforgeryCookie` | Whether the caller sent an anti-forgery cookie |
| `UserAgent` | Cut to `MaximumUserAgentLength`. Absent when none was sent |

Always a Warning. `LoggingContract.AntiforgeryRefusalProperties` is this list, and
`AntiforgeryRefusalOptionalProperties` is the part an event can lack, `UserAgent`.

**`web.form.refused`** is a form post that came back to its page with errors.
`FormRefusalLogFilter` is a page filter, so no page has to remember. It fires on a POST whose
handler returned the page with invalid model state. A page that redirects writes no line.

| Property | Value |
| --- | --- |
| `Event` | `web.form.refused` |
| `PageName` | The page's view engine path, such as `/Auth/Login` |
| `PageHandler` | The handler name, or `(default)` for the unnamed one |
| `InvalidFields` | The model state keys with an error, sorted. `(form)` for a page-wide error |
| `FormErrors` | The messages the page showed, at most 20, each cut to 300 characters |
| `FollowsEvent` | The refusal event already logged in this request. Absent when there was none |
| `FollowsRequest` | The request that refusal was for. Absent when there was none |

Every posted value of three or more characters is cut out of `FormErrors` and replaced by
`…`, because a model-binding message quotes the value that failed to bind. Shorter values are
left. This is one control on one property, not general redaction: a message that holds a
person's name the form did not post still reaches the log, which is what the seam below is
for.

`LoggingContract.FormRefusalProperties` is this list, and `FormRefusalOptionalProperties` is
the part an event can lack: `FollowsEvent` and `FollowsRequest`. `FollowsRequest` is never
present without `FollowsEvent`.

### Category and filter order

Both events are logged under the package's own categories, which Serilog writes as
`SourceContext`:

- `Solrevdev.Logging.AspNetCore.AntiforgeryRefusalLogFilter`
- `Solrevdev.Logging.AspNetCore.FormRefusalLogFilter`
- `Solrevdev.Logging.AspNetCore.AntiforgeryRefusalEndpointFilter`, for the endpoint filter

If you are replacing filters of your own, a `MinimumLevel:Override` or a saved Seq query on
their old category no longer matches. Query on `Event`, which does not move. A level override
on `Solrevdev.Logging.AspNetCore` set above Information hides the form line that follows a
logged refusal, and above Warning hides both events.

`AddSolrevdevRefusalLogging` appends the two filters to `MvcOptions.Filters` where it is
called, so their order among your own global filters follows the order of the calls. For most
applications it does not matter. It matters in three cases, and in no other:

- **A page filter of yours registered BEFORE it that replaces the result after the handler.**
  The form filter runs inside yours, so it decides on the handler's result and not on your
  replacement. Register yours after, and the form filter sees the result you set.
- **A filter of yours that short-circuits before the page handler.** The handler never
  returns the page, so there is no form line, wherever the form filter sits.
- **A result filter of yours registered before it that replaces the anti-forgery failure
  result.** The anti-forgery filter is an always-run result filter, so a short-circuit does
  not hide the failure from it, but it looks at the result as it stands when its turn comes.

`ILoggedRefusal` is not a matter of order. It is read after the handler has returned, so
anything the handler wrote is there.

### Counting a refusal once

A form often comes back because something else refused the request and already logged a
Warning: a command pipeline, or a sign-in page. A second Warning would count one refusal
twice. So the form line is a **Warning when it is the only record** and **Information when it
follows a logged refusal**, in which case it carries `FollowsEvent`.

The filter learns that from `ILoggedRefusal`, resolved from the request services:

```csharp
public interface ILoggedRefusal
{
    string? LoggedEvent { get; }        // the event already written, or null
    string? LoggedRequestName { get; }  // its request, such as a command name, or null
    string? ShownMessage { get; }       // what the page shows, where the log got other text
    string? LoggedMessage { get; }      // that other text
}
```

With none registered, every form refusal is a Warning with no `FollowsEvent`.

`AddSolrevdevLoggedRefusal` registers the default, `LoggedRefusal`, one per request, under
both types. They are the same object in a request:

- **Inject `LoggedRefusal` to write**, wherever you log a refusal, and call it straight after.
- **Inject `ILoggedRefusal` to read.** The filter does, and so can a page of your own.

```csharp
logger.LogWarning("{Event} sign-in refused for {Email}", MyEvents.AuthSignInRefused, email);
loggedRefusal.Logged(MyEvents.AuthSignInRefused);
```

Code that also runs where nothing registered it, such as a command handler shared with a
worker, takes it as an optional constructor parameter. The container passes null where it is
not registered, and the handler logs the refusal either way:

```csharp
public sealed class ReserveOrderNumberHandler(ILogger<ReserveOrderNumberHandler> logger, LoggedRefusal? loggedRefusal = null)
{
    private void Refuse(string reason)
    {
        logger.LogWarning("{Event} refused: {RefusalReason}", MyEvents.RequestRefused, reason);
        loggedRefusal?.Logged(MyEvents.RequestRefused, nameof(ReserveOrderNumberCommand));
    }
}
```

`ShownMessage` and `LoggedMessage` cover the case where the page's message holds a name and
the first line logged fixed text in its place. The page copies its message into the form
errors, so the filter swaps one for the other before logging.

An application whose refusal state lives in a project that cannot reference this package
registers a scoped adapter instead, and does not call `AddSolrevdevLoggedRefusal`:

```csharp
builder.Services.AddScoped<ILoggedRefusal, MyRefusalAdapter>();
```

### Outside MVC

The global MVC filter sees MVC and Razor Pages only. A minimal-API endpoint writes the same
`web.antiforgery.refused` event through `AntiforgeryRefusalLog`, which is what the MVC filter
itself calls, so there is one implementation: the same template, properties, cookie test and
user agent cut.

```csharp
app.MapPost("/orders/add", async (HttpContext httpContext, IAntiforgery antiforgery, ILogger<Orders> logger) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(httpContext);
    }
    catch (AntiforgeryValidationException exception)
    {
        AntiforgeryRefusalLog.Write(logger, httpContext, exception);
        return Results.BadRequest();
    }

    return Results.Ok();
});
```

`Write` returns false and writes nothing when the event was already written for the request,
so two callers on one request count one refusal once. `WriteAsync(logger, httpContext)` is for
code that knows the check failed and has no exception: it validates again to read the reason.

For a route group whose endpoints let the exception out, `AntiforgeryRefusalEndpointFilter`
logs it and throws it on:

```csharp
var orders = app.MapGroup("/orders").AddEndpointFilter<AntiforgeryRefusalEndpointFilter>();
```

The endpoint filter cannot see two cases, both measured. An endpoint that catches the
exception must call `Write` in its catch. An endpoint that binds a form and leaves the check
to `UseAntiforgery` is answered 400 by the framework before any endpoint filter runs. For
that one, add this after `UseAntiforgery`:

```csharp
app.UseAntiforgery();
app.Use(async (httpContext, next) =>
{
    if (httpContext.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false } refused)
    {
        AntiforgeryRefusalLog.Write(logger, httpContext, refused.Error as AntiforgeryValidationException);
    }

    await next(httpContext);
});
```

The package does not ship that middleware. The feature is set for every endpoint the
middleware checks, and whether an invalid one is then refused depends on the endpoint, so
only the application knows that logging it there is right.

## Event names

`LoggingContract.Event` is the property an application puts a stable event name in. The
package never sets it. The shape is `area.thing.outcome`: lowercase, exactly three parts,
dots between parts and underscores allowed inside one, such as `auth.signin.succeeded`.

A mistyped name fails quietly, because it never matches a query. `EventNames.Violations`
checks every public string constant on a type and returns what is wrong as data, so your own
test asserts on it with whatever test framework you use:

```csharp
[Fact]
public void Every_event_name_is_well_formed_and_unique()
{
    var violations = EventNames.Violations(typeof(MyEvents), nameof(MyEvents.PropertyName));

    Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
}
```

Pass the names of constants that are not event names as the later arguments.
`EventNames.IsWellFormed(name)` checks one name.

The package writes two event names of its own, `RefusalEvents.WebAntiforgeryRefused` and
`RefusalEvents.WebFormRefused`. Do not reuse either value in your vocabulary.

## OpenTelemetry names

The log property names are deliberately not renamed to the OpenTelemetry ones: Seq queries a
dotted name awkwardly, and logs and traces already join on the trace id. This table is for
reading one beside the other, and for naming span attributes and the trace resource.

| Log property | OpenTelemetry name |
| --- | --- |
| `ClientIp` | `client.address` |
| `UserAgent` | `user_agent.original` |
| `RequestMethod` | `http.request.method` |
| `RequestPath` | `url.path` |
| `RequestQuery` | `url.query` |
| `StatusCode` | `http.response.status_code` |
| `RequestHost` | `server.address` |
| `RequestScheme` | `url.scheme` |
| `UserId` | `user.id` (not yet stable) |
| `UserEmail` | `user.email` (not yet stable) |
| `Application` | `service.name` |
| `Environment` | `deployment.environment.name` |
| `MachineName` | `host.name` |
| `TraceId` | The trace id of the span context, `trace_id` in OTLP |
| `SpanId` | The span id of the span context, `span_id` in OTLP |

`user.id` and `user.email` were both marked Development, not Stable, in the OpenTelemetry
attribute registry when this was written on 25 Nov 2026. Check before relying on either.
`deployment.environment.name` was checked the same day and is Stable. The other rows were not
re-checked.

Two rows are not the same value under another name. `RequestQuery` is the allowlisted cut,
where `url.query` on a span is whatever the instrumentation recorded. `RequestHost` is the
whole Host header, port included, where `server.address` is the name alone and the port is
`server.port`.

## Client addresses behind a load balancer

`ClientIp` is `Connection.RemoteIpAddress` after ASP.NET Core has processed forwarded headers.
This package formats that address for reading. It does not choose it, and it never reads a
forwarding header to decide it.

With forwarded headers configured as above, a correctly trusted single-hop balancer gives the
client address the balancer appended. An extra entry written by the caller sits to the left of
that one and is not taken. If the trust list does not cover the balancer, `ClientIp` stays as
the balancer's own address, so a wrong trust list looks like every request coming from one
place.

`ClientIp` is the address at the end of that network path. It can be a NAT gateway, a VPN exit
or another proxy, and it does not identify a person or a device.

`ForwardedFor` is separate, untrusted and for reading only. A difference between it and
`ClientIp` is a reason to look, not proof of spoofing. The reconstruction is described below
and it loses information in ordinary cases.

## The traps this package holds

**`::1` is not an IPv4-mapped address.** `IPAddress.Parse("::1").IsIPv4MappedToIPv6` is
`false` on .NET 10, measured, because mapping means the `::ffff:0:0/96` prefix and loopback is
its own address. The mapped check alone leaves every local request reading `::1` while the same
machine over IPv4 reads `127.0.0.1`. The explicit `IPAddress.IsLoopback` test catches it, and
it covers all of 127.0.0.0/8, so `127.0.0.2` renders as `127.0.0.1` too.

**`MapToIPv4` does not throw on a genuine IPv6 address. It fails silently.** It returns the
last four bytes as a plausible, routable, completely wrong IPv4 address. Measured on .NET 10 in
this repository's test suite:

| Input | `IsIPv4MappedToIPv6` | `MapToIPv4()` |
| --- | --- | --- |
| `2001:db8:85a3::8a2e:370:7334` | `false` | `3.112.115.52` |
| `::1` | `false` | `0.0.0.1` |
| `fe80::1` | `false` | `0.0.0.1` |
| `::ffff:127.0.0.2` | `true` | `127.0.0.2` |

Without the `IsIPv4MappedToIPv6` guard, every genuine IPv6 caller would be logged under a
fabricated address and nothing would raise. `3.112.115.52` in that table is not an example
address. It is the measured output of truncating the documentation address above it.

The two checks run in a fixed order and the order is behaviour. The mapped check runs first.
`IPAddress.IsLoopback("::ffff:127.0.0.2")` is `false` on the raw value and `true` once the
mapping is dropped, so testing loopback first sends that caller down the mapped branch and logs
it as `127.0.0.2` instead of `127.0.0.1`. That one input is the only case in the suite that
tells the two orderings apart, and it is pinned by a test.

**`UseForwardedHeaders` consumes the entries it accepts.** Send one `X-Forwarded-For` entry and
the middleware moves it onto the connection and deletes the now empty header, so reading the
header afterwards finds nothing. One entry is what a single load balancer sends, so the case
that recorded nothing is the normal case. `X-Original-For` holds the peer that was replaced,
with its port, so it is not the missing half either.

`ForwardedForHeader.Claimed` therefore rebuilds an approximation: whatever is left in the
header, plus the address the middleware settled on. Four things it cannot do, each measured:

- `X-Original-For` is a hint that the middleware acted, not evidence of it. Any caller can send
  that header. An untrusted caller sending it and no `X-Forwarded-For` is reported as having
  forwarded itself. `ClientIp` is unaffected, because it comes off the connection.
- A port is lost. A trusted `X-Forwarded-For: 203.0.113.42:54321` comes back as
  `203.0.113.42`, because the port went onto `Connection.RemotePort`.
- Only the last consumed entry is recoverable. With two trusted hops consumed,
  `203.0.113.42,203.0.113.11` comes back as `203.0.113.42` and the middle hop is gone.
- The recovered entry is the framework's spelling, not the caller's characters. IPv6 text is
  normalised, and the default header names are assumed, so renaming them through
  `ForwardedHeadersOptions` leaves only the header remainder.

Treat `ForwardedFor` as a clue. Never use it for identity or access.

**`Uri.TryCreate` accepts a bare path as a `file://` URI.** Rebuilding from its parts escapes
the query into the path rather than dropping it, so `/auth/resetpassword?id=live-token` becomes
`file:///auth/resetpassword%3Fid=live-token` and the token survives. That is the outcome referer
sanitising exists to prevent, so only `http` and `https` take the URI route and everything else
is cut by hand at the first `?` or `#`. It is unit-tested as a security control.

The guarantee is specific: the query string and the fragment are removed. That is not general
secret redaction. A path segment can still carry an identifier, so it is worth knowing what
your own URLs put in a path.

## What this package must never grow

No rate limiting. No throttling. No `ForwardedHeadersOptions` policy. Nothing that keys off an
address for any purpose other than a log property. No OpenTelemetry setup, no EF Core and no
MediatR: it references Serilog.AspNetCore and the ASP.NET Core shared framework and nothing
else.

Since 1.3.0 it does hold two MVC filters, for the [refusal events](#refusal-events). MVC is
part of the shared framework, so they brought no dependency. They log and do nothing else:
neither changes a response.

Which peers to trust and how to bucket a limiter are the consuming application's decisions, and
they matter: a whole office typically leaves through one NAT address.

`ClientAddress.Describe` reshapes an address to be readable, which makes it wrong for anything
that has to be stable and unique per caller. Never use its result for a rate limit, a throttle,
a cache partition, an authorisation decision or a stored column. The authorisation item is in
that list on purpose. An address-based gate is an ordinary thing to find in an application:
admitting an office range, unlocking an administrative surface on the local machine, or varying
what a sign-in page tells an unauthenticated caller. Fed this value, any of them is widened by
the loopback collapse and moved by the mapped rewrite, and it fails open rather than loudly.

For every address that arrives in native, non-mapped, non-loopback form the reshaped and raw
strings are identical, which is what lets the mistake survive testing. The scope is exactly
that, and not "every routable address": an IPv4 caller arriving on a dual-stack socket as
`::ffff:203.0.113.42` is routable, and this helper renders it as `203.0.113.42` while
`ToString()` on the same address gives the mapped spelling.

### Pin the call sites

That rule is otherwise enforced by a person reading a diff. Make it a build step instead.

```bash
# ClientAddress.Describe is for log properties only. Two call sites, both inside this
# package's own middleware. A third is somebody extending it into a partition key, a
# cache key, an authorisation decision or a database column, which is the one mistake
# that survives testing because the strings match for every native, non-mapped caller.
expected=2
actual=$(grep -rc --include='*.cs' 'ClientAddress\.Describe(' src \
  | awk -F: '{ n += $2 } END { print n + 0 }')

if [ "$actual" != "$expected" ]; then
  echo "ClientAddress.Describe call sites: $actual, expected $expected" >&2
  echo "A new call site needs a reason. See rule 2 in AGENTS.md." >&2
  exit 1
fi
```

In this repository the expected count is **2**: `ClientLogContextMiddleware` and
`RequestSummaryEnricher`. In a consuming application it is usually **0**, because the
middleware does the rendering and nothing else should call the helper at all. Set `expected=0`
and the check reports the first time somebody reaches for it. Move the number in the same
commit as the call site, never separately.

## Options

`RequestLogOptions` is deliberately small. Property names are not configurable, because
divergent names are what this package exists to end.

| Option | Default | What it does |
| --- | --- | --- |
| `UserIdClaimType` | `ClaimTypes.NameIdentifier` | Which claim carries the user id |
| `UserEmailClaimType` | `ClaimTypes.Email` | Which claim carries the email address |
| `FallBackToIdentityNameForEmail` | `true` | Use `Identity.Name` when the email claim is blank |
| `UserIdSelector` | none | Replaces the id lookup entirely |
| `UserEmailSelector` | none | Replaces the email lookup entirely |
| `QuietPathPrefixes` | `/healthcheck`, `/version` | Successful requests under these are Verbose |
| `QuietFileExtensions` | css, js, images, fonts and similar | Successful static files are Verbose |
| `MaximumUserAgentLength` | 512 | Cap on the logged user agent |
| `MaximumRefererLength` | 512 | Cap on the sanitised referer |
| `MaximumForwardedForLength` | 256 | Cap on the reconstructed forwarding claim |
| `IncludeNavigation` | `false` | Write `Navigation` on the request summary |
| `IncludeRequestQuery` | `false` | Write `RequestQuery` on the request summary |
| `IncludeSpanId` | `false` | Write `SpanId` beside `TraceId` |
| `RequestQueryKeys` | empty | Query keys whose values may be logged, compared without regard to case |
| `MaximumRequestQueryPairs` | 12 | Most `key=value` pairs kept |
| `MaximumRequestQueryValueLength` | 50 | Longest value logged for a listed key. A longer one is withheld, not cut |
| `SummaryEnrichers` | empty | Your own summary fields, run after the package's |

```csharp
builder.Services.AddSolrevdevRequestLogging(options =>
{
    options.UserEmailSelector = principal => principal.FindFirst("staff_email")?.Value;
    options.QuietPathPrefixes.Add("/metrics");
});
```

The three header caps and the two query caps must be at least 1. A cap of zero or less is rejected at startup, because zero
means the property never appears and that reads exactly like the enricher being broken. The
helpers themselves return nothing rather than throwing if they are given such a cap, so a
misconfiguration cannot fail a request from inside a log enricher.

`QuietPathPrefixes` is matched with `StartsWithSegments`, so `/versionhistory` is not quietened
by `/version`.

The email fallback to `Identity.Name` covers a measured case. A fourteen-day cookie issued
before an application started emitting an Email claim still has to resolve to something, or
every session issued before that change logs no email until it expires. One of the source
applications puts the address in the Name claim and has never issued an Email claim.

The type is `RequestLogOptions` rather than `RequestLoggingOptions` because
`Serilog.AspNetCore` already owns that second name in the same conceptual space.

## Testing your application against the contract

`LoggingContract` holds the property names as constants and as sets. The sets say which
component owns a name. They do not say that every request carries every name.

- `EnricherProperties`: what this package sets.
- `HostProperties`: what Serilog's request logging and your enrichers set.
- `RequiredProperties`: both of those together, the full contract.
- `AuthenticatedProperties`: `UserId` and `UserEmail`, which must not appear on an anonymous
  request.
- `OptionalProperties`: the names a legitimate request can arrive without.
- `OptInProperties`: `Navigation`, `RequestQuery` and `SpanId`, each set only when its option
  is on.
- `ExtendedProperties`: `OptInProperties` and `Event`.
- `AntiforgeryRefusalProperties` and `FormRefusalProperties`: everything each
  [refusal event](#refusal-events) can carry, with `AntiforgeryRefusalOptionalProperties` and
  `FormRefusalOptionalProperties` for the part each can lack. Assert an event against its set
  minus its optional set, as for the request summary.
- `RefusalProperties`: the eight names the two events added, across both.

The first five sets are as they were in 1.2.0, and no name will be added to them.
Applications assert the older sets on every request, so a new name there would fail their
tests on upgrade. Assert an opt-in name only in a scenario that produces it.

Assert unconditionally against `RequiredProperties` minus `OptionalProperties`, then assert the
optional names in the scenarios that actually produce them.

```csharp
[Fact]
public void Every_request_summary_carries_the_unconditional_contract()
{
    var summary = /* the request summary event your test host produced */;

    Assert.All(
        LoggingContract.RequiredProperties.Except(LoggingContract.OptionalProperties),
        name => Assert.Contains(name, summary.Properties.Keys));
}
```

`Referer`, `UserAgent` and `ForwardedFor` are caller-supplied headers that an API client or a
health probe will not send. `ClientIp` needs a connection, which a request built in a test does
not have. `TraceId` needs an `Activity`. `UserId` and `UserEmail` need both an authenticated
principal and the claim. Requiring all of them on every request fails on traffic that is doing
nothing wrong, and the usual fix for a test like that is to start writing empty values, which
gives up the absent-not-empty convention for a test's convenience.

Worth covering in your own suite: an anonymous request, a signed-in request, an authorization
refusal, a rate-limited request, and forwarded addresses from both a trusted and an untrusted
peer. This package's tests prove the library. They cannot prove your deployed proxy
configuration.

### A bare DefaultHttpContext gets package defaults

`RequestLogLevel.For(context, exception)` and
`RequestSummaryEnricher.EnrichRequestSummary(diagnosticContext, context)` read their options
from `HttpContext.RequestServices`. When that is null, or when the application wired the
middleware without calling `AddSolrevdevRequestLogging`, they fall back to a default
`RequestLogOptions` and say nothing about it.

A `DefaultHttpContext` built in a test has no `RequestServices`. A test using these overloads
therefore measures the package defaults while reading as though it measures your application's
configuration. If your application clears `QuietPathPrefixes` so that `/version` keeps its
line, the test still sees `Verbose`, and the natural conclusion from that measurement is that
the overloads never consult DI at all.

Attach the services, or pass the options:

```csharp
var services = new ServiceCollection();
services.AddSolrevdevRequestLogging(options => options.QuietPathPrefixes.Clear());

var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
context.Request.Path = "/version";

Assert.Equal(LogEventLevel.Information, RequestLogLevel.For(context, null));
```

The overloads that take a `RequestLogOptions` argument have no such fallback, so use those
when the test is about the options rather than about the wiring.

The other route to the same fallback is an application that wires the middleware without
calling `AddSolrevdevRequestLogging`, and that one is refused at startup. It used to start and
run on package defaults it never chose, because `IOptions<RequestLogOptions>` resolves to a
default instance whether or not the package was registered.

The startup guard covers the application. It does not cover the test above, because no
application starts there. Those are two halves of one problem and only one of them can be
checked automatically.

## Build and test

```bash
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack src/Solrevdev.Logging.AspNetCore --configuration Release
```

`global.json` asks for SDK 10.0.100 with `rollForward: latestMajor` and no prereleases, so the
newest installed SDK builds. SDK 11 or later builds `net10.0` and `net11.0`; SDK 10 alone builds
`net10.0` only. Testing `net10.0` under SDK 11 needs the .NET 10 runtime too. `TreatWarningsAsErrors` and `GenerateDocumentationFile` are both on, so
a public member without XML docs fails the build.

## Maintainers

Release process, publishing setup, test package choices and where the code came from are in
[MAINTAINING.md](MAINTAINING.md). Rules for changing this repository are in
[AGENTS.md](AGENTS.md).

## Licence

MIT. Copyright (c) solrevdev.
