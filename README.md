<div align="center">
  <img src="docs/assets/branding/logging-icon.svg" alt="solrevdev.Logging.AspNetCore" width="128" height="128">
  <h1>solrevdev.logging</h1>
  <p><strong>Shared Serilog request logging for ASP.NET Core.</strong> One set of property names, one request summary and one set of rules about what is logged, so several applications writing into one Seq mean the same thing by the same query.</p>
  <p>
    <a href="https://www.nuget.org/packages/solrevdev.Logging.AspNetCore/"><img src="https://img.shields.io/nuget/v/solrevdev.Logging.AspNetCore.svg?logo=nuget" alt="NuGet"></a>
    <a href="https://www.nuget.org/packages/solrevdev.Logging.AspNetCore/"><img src="https://img.shields.io/nuget/dt/solrevdev.Logging.AspNetCore.svg" alt="NuGet downloads"></a>
    <a href="https://github.com/solrevdev/solrevdev.logging/actions/workflows/ci.yml"><img src="https://github.com/solrevdev/solrevdev.logging/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
    <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
    <img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT">
  </p>
</div>

---

## Contents

- [Why this exists](#why-this-exists)
- [Install](#install)
- [Quick start](#quick-start)
- [Set up Serilog](#set-up-serilog)
- [Enable forwarded headers](#enable-forwarded-headers)
- [Wire up the middleware](#wire-up-the-middleware)
- [What gets logged](#what-gets-logged)
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

Targets `net10.0`. Depends on `Serilog.AspNetCore` 10.0.0 and the ASP.NET Core shared
framework.

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
them.

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
address for any purpose other than a log property.

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

```csharp
builder.Services.AddSolrevdevRequestLogging(options =>
{
    options.UserEmailSelector = principal => principal.FindFirst("staff_email")?.Value;
    options.QuietPathPrefixes.Add("/metrics");
});
```

The three caps must be at least 1. A cap of zero or less is rejected at startup, because zero
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

The SDK is pinned by `global.json` to 10.0.100 with `rollForward: latestFeature`, matching the
consuming applications. `TreatWarningsAsErrors` and `GenerateDocumentationFile` are both on, so
a public member without XML docs fails the build.

## Maintainers

Release process, publishing setup, test package choices and where the code came from are in
[MAINTAINING.md](MAINTAINING.md). Rules for changing this repository are in
[AGENTS.md](AGENTS.md).

## Licence

MIT. Copyright (c) solrevdev.
