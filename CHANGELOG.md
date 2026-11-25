# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.0] - 2026-11-25

Additions only. No existing API changed and no existing output changed: no public name was
removed or renamed, no existing `LoggingContract` constant or set changed its value or
membership, and an application that upgrades from 1.2.0 without touching its configuration
writes the same properties with the same values. Every new property and both filters are off
until switched on.

**New public names were added, and that can break a build.** An application with a type of
its own that has one of the names below, in a file that imports
`Solrevdev.Logging.AspNetCore`, gets CS0104 (ambiguous reference) from the version bump alone.
That is most likely for a local copy of a type this package was ported from. Delete the copy
or alias it. Nothing changes at run time. The nineteen new public type names:

`AntiforgeryRefusalEndpointFilter`, `AntiforgeryRefusalLog`, `AntiforgeryRefusalLogFilter`,
`BootstrapHost`, `BootstrapLogIdentity`, `EventNameViolation`, `EventNameViolationKind`,
`EventNames`, `FormRefusalLogFilter`, `HeaderValue`, `ILoggedRefusal`, `LoggedRefusal`,
`RefusalEvents`, `RefusalLogOptions`, `RefusalLoggingExtensions`, `RequestNavigation`,
`RequestQuery`, `SelfLogOutput`, `TransportExceptionLog`.

### Added

- `RequestLogOptions.SummaryEnrichers`, a list of `Action<IDiagnosticContext, HttpContext>` for
  an application's own request summary fields. They run after the package's own, in the order
  added, on every path that reaches `RequestSummaryEnricher.Enrich`. An enricher that throws is
  caught and written to Serilog's `SelfLog`, and the rest still run. Measured against
  Serilog.AspNetCore 10.0.0, an exception from `EnrichDiagnosticContext` otherwise fails the
  request after its page has run and no summary line is written.
- `Navigation`, how the browser says the request was started, from the `Sec-Fetch-*` headers:
  `fetch`, `frame`, `direct`, `cross-site`, `user` or `scripted`, and absent when none of the
  headers was sent. Opt in with `RequestLogOptions.IncludeNavigation`. `RequestNavigation`
  holds the values as constants and a public `Describe`.
- `RequestQuery`, the query string cut down to an allowlist. Opt in with
  `RequestLogOptions.IncludeRequestQuery`. `RequestQueryKeys` is the allowlist and starts
  empty, so every value reads `(set)` until a key is listed.
  `MaximumRequestQueryPairs` (12) and `MaximumRequestQueryValueLength` (50) are the caps, and
  both are refused at startup when less than 1. `RequestQuery.Describe` is public.
- `SpanId`, the request span's id from `Activity.Current`, beside `TraceId` on the request
  summary and on the client log context. Opt in with `RequestLogOptions.IncludeSpanId`.
- `LoggingContract.Navigation`, `RequestQuery`, `SpanId` and `Event`, with two new sets:
  `OptInProperties` (the first three) and `ExtendedProperties` (all four). None is in any
  earlier set.
- `SelfLogOutput`, which writes one line the first time the Seq sink cannot connect, nothing
  for later connection failures, and every other SelfLog message whole. The closing sentence
  is a constructor argument, and so is an optional prefix for the first line of each message.
  Known gap: once per process.
- `BootstrapLogIdentity.Resolve`, which reads `Application` and `Environment` from launch
  arguments and environment variables only, with the default application name passed in.
  `BootstrapHost` says which host the application builds, because the hosts read
  `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` and `--environment` in different orders. The
  default, `WebApplication`, is the order `WebApplication.CreateBuilder` uses, measured:
  argument, `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT`.
- `AntiforgeryRefusalLog.Write` and `WriteAsync`, the one writer of `web.antiforgery.refused`,
  for a minimal-API endpoint or any code outside MVC. The MVC filter calls it too. It writes
  at most once per request.
- `AntiforgeryRefusalEndpointFilter`, an endpoint filter for a route or route group that logs
  an `AntiforgeryValidationException` the endpoint lets out and throws it on.
- `TransportExceptionLog.Snapshot` and `IsTransportFailure`, which keep the type, the HTTP
  status and the stack frames of a failed outbound call and drop its response text and URL.
- `EventNames.IsWellFormed` and `EventNames.Violations`, with `EventNameViolation` and
  `EventNameViolationKind`: the `area.thing.outcome` convention, and a check of a type's public
  string constants that returns its findings as data. The package still references no test
  framework.
- `HeaderValue` and `HeaderValue.Truncate` are public, so an application can cut a
  `UserAgent` it logs itself to the configured length. They were internal.
- `AddSolrevdevRefusalLogging`, which adds two MVC filters as global filters. Opt-in: an
  application that does not call it logs what it logged before. `RefusalLogOptions` switches
  either off. MVC is in the ASP.NET Core shared framework, so there is no new dependency.
  - `AntiforgeryRefusalLogFilter` writes `web.antiforgery.refused`, a Warning, for a form post
    that failed its anti-forgery check, with `RequestMethod`, `RequestPath`, `RefusalReason`,
    `HasAntiforgeryCookie` and `UserAgent` cut to `MaximumUserAgentLength`.
  - `FormRefusalLogFilter` writes `web.form.refused` for a POST whose handler returned the page
    with invalid model state, with `PageName`, `PageHandler` (`(default)` for the unnamed
    handler), `InvalidFields` (`(form)` for a page-wide error) and `FormErrors`. Posted values
    of three or more characters are cut out of the messages, which are capped at 300
    characters each and 20 in all. It is a Warning when it is the only record, and Information
    with `FollowsEvent` and, where there was one, `FollowsRequest` when a refusal was already
    logged in the request.
- `ILoggedRefusal`, the read-only seam the form filter resolves from the request services to
  learn of a refusal already logged, and `LoggedRefusal`, a scoped default registered by
  `AddSolrevdevLoggedRefusal`. With neither registered every form refusal is a Warning.
- `RefusalEvents.WebAntiforgeryRefused` and `RefusalEvents.WebFormRefused`, the two event names
  the package writes.
- `LoggingContract.PageName`, `PageHandler`, `InvalidFields`, `FormErrors`, `FollowsEvent`,
  `FollowsRequest`, `RefusalReason` and `HasAntiforgeryCookie`, in a new set,
  `RefusalProperties`. None is in any other set.
- `LoggingContract.AntiforgeryRefusalProperties` and `FormRefusalProperties`, everything each
  refusal event can carry, with `AntiforgeryRefusalOptionalProperties` (`UserAgent`) and
  `FormRefusalOptionalProperties` (`FollowsEvent`, `FollowsRequest`) for what each can lack.
- A README table from each contract name to its OpenTelemetry semantic-convention name, and a
  policy in `MAINTAINING.md` for adding a property, never renaming one, and what a consumer
  does on upgrade.

### Changed

- `UseSolrevdevRequestLogging` reads the registered `RequestLogOptions` from the
  application's service provider and no longer looks in `HttpContext.RequestServices` on each
  request. Under a host those are the same options, so the output is the same. One application
  bypassed the helper on the belief that it used package defaults; that was true only of the
  two short overloads called with no request services, which a host never does, and it cannot
  be true of the helper now. The two short overloads behave as before.

## [1.2.0] - 2026-11-12

Build and packaging only. No code, no public name and no behaviour changed.

### Changed

- The package now targets `net11.0` beside `net10.0`. The three consuming applications stay on
  `net10.0`, which the package still serves.
- .NET 10 is the LTS baseline for building. `global.json` asks for SDK `10.0.100` with
  `rollForward: latestMajor` and `allowPrerelease: false`, so the newest installed release SDK
  builds. SDK 11 or later builds both targets; SDK 10 alone builds `net10.0` only. CI installs
  SDKs 10.0.x and 11.0.x and fails if the package is missing either target.

## [1.1.1] - 2026-09-20

Packaging and documentation only. No code, no public name and no behaviour changed.

### Added

- A package icon. `docs/assets/branding/logging-icon.svg` is the source and
  `logging-icon-128.png`, rendered from it with `rsvg-convert`, is what `PackageIcon` ships, so
  nuget.org shows a mark rather than the default placeholder. The two move together.
- A README header, contents list and quick start, matching the sibling `solrevdev.bbx`
  repository, so the pipeline order a consumer has to get right is visible without scrolling
  the whole file.

## [1.1.0] - 2026-09-08

Corrections from an independent review of 1.0.0. No public name was removed or renamed.

### Added

- A startup guard on `UseSolrevdevClientLogContext`, `UseSolrevdevUserLogContext` and
  `UseSolrevdevRequestLogging`. Each now throws `InvalidOperationException` when
  `AddSolrevdevRequestLogging` was not called, using the framework's wording: "Unable to find
  the required services. Please add all the required services by calling
  'IServiceCollection.AddSolrevdevRequestLogging' in the application startup code." It uses the
  marker-service pattern ASP.NET Core uses for routing, MVC and SignalR, because
  `IOptions<RequestLogOptions>` cannot answer the question: every host calls `AddOptions`, so
  the options resolve to a default instance whether or not this package was registered.
- `LoggingContract.OptionalProperties`, the property names a legitimate request can arrive
  without, so a consuming application asserts `RequiredProperties` minus this set
  unconditionally and the rest per scenario. `RequiredProperties` and
  `AuthenticatedProperties` are unchanged.
- Startup validation of the three header caps in `AddSolrevdevRequestLogging`, through
  `ValidateOnStart`. A cap of zero or less is now refused with a message naming the option.
- Regression tests pinning how `RequestLogLevel.For(context, exception)` and
  `RequestSummaryEnricher.EnrichRequestSummary` find their options: the application's
  configuration through a populated `RequestServices`, and the package defaults when there is
  no `RequestServices` or no registration.
- Regression tests that run the real `ForwardedHeadersMiddleware` and assert real Serilog
  events: trusted single-hop forwarding, a forged extra entry, an untrusted direct peer, a
  forged `X-Original-For`, forwarding not enabled, a dual-stack proxy peer, a mapped client
  entry, a genuine IPv6 client, a client entry with a port, two trusted hops, a short circuit
  before the user context, and which log scope is open when the summary is written.

### Fixed

- A `MaximumUserAgentLength`, `MaximumRefererLength` or `MaximumForwardedForLength` of less
  than zero threw `ArgumentOutOfRangeException` from inside the log enricher, during a
  request. The helpers now return no value for a cap that keeps nothing, and the
  misconfiguration is refused at startup instead.
- An authenticated claim holding an empty string put `UserId=""` on every event inside the
  request while the request summary omitted the property. Blank now means absent in both
  places, including for a custom selector that returns an empty string. A value with content
  is never trimmed.

### Changed

- **An application that wires this package's middleware without calling
  `AddSolrevdevRequestLogging` now fails at startup instead of running.** This is deliberate.
  Before it, such an application started, served requests and logged every one of them with
  package defaults it never chose, and nothing anywhere said so. The fix is to add the missing
  `builder.Services.AddSolrevdevRequestLogging()` call. The guard covers the application only.
  It cannot cover a unit test on a bare `DefaultHttpContext`, which still measures package
  defaults silently, because no application starts there.
- The silent fallback to package defaults in `RequestLogLevel.For(context, exception)` and
  `RequestSummaryEnricher.EnrichRequestSummary` is documented rather than left to be
  discovered: what triggers it, that a test on a bare `DefaultHttpContext` measures package
  defaults, and that the options-taking overloads are the way to be certain. The behaviour is
  unchanged.
- `ForwardedFor` is documented as what it is: a capped, best-effort reconstruction of untrusted
  forwarding information. `X-Original-For` is a hint that the middleware acted rather than
  evidence of it, and the reconstruction loses ports, intermediate consumed hops and the
  caller's own spelling of an address. The algorithm is unchanged, so the property's value is
  unchanged. `ClientIp` was and remains taken from the connection.
- The README is rewritten for somebody consuming the package, and now covers the two
  prerequisites it never mentioned: `Enrich.FromLogContext()` on the application's own logger,
  and enabling and configuring forwarded-header processing with `KnownIPNetworks`. Release and
  maintainer material moved to `MAINTAINING.md`.
- The release workflow pushes the git tag before the package, and a retry against an existing
  tag now checks that tag out before packing. This does not make nuget.org and Git atomic. It
  chooses which side is left dangling when a release does not complete.

## [1.0.0] - 2026-09-08

First release. Extracted from three internal ASP.NET Core applications that had each
hand-copied and then diverged from the same request-logging code, and merged as the union of
the three rather than the smallest common part.

### Added

- `ClientAddress.Describe` — renders a connection address for a log property. Drops the
  IPv4-mapped prefix, collapses all of 127.0.0.0/8 to `127.0.0.1`, keeps a link-local address's
  zone suffix, and returns a public IPv6 address untouched rather than putting it through
  `MapToIPv4`, which silently truncates it to a wrong but plausible IPv4 address. The mapped
  check runs before the loopback test, and that order is pinned by a test.
- `ForwardedForHeader.Claimed` — reassembles the `X-Forwarded-For` the caller actually sent,
  recovering the entry `UseForwardedHeaders` consumed from the connection it was put on.
- `RefererHeader.Sanitise` — reduces a referer to scheme, host and path, so a password-reset
  or email-confirmation token in the query string never reaches the log store.
- `RequestLogLevel` — demotes successful static-file, `/healthcheck` and `/version` request
  summaries to Verbose, with the paths and extensions configurable.
- `ClientLogContextMiddleware` — pushes `ClientIp` and `TraceId` for every request, above the
  rate limiter.
- `UserLogContextMiddleware` — pushes `UserId` and `UserEmail`, only when authenticated.
- `RequestSummaryEnricher.EnrichRequestSummary` — the `EnrichDiagnosticContext` delegate for
  `UseSerilogRequestLogging`.
- `LoggingContract` — the required property names as constants and as sets, so a consuming
  application can test that its summaries have not drifted.
- `RequestLogOptions` with `AddSolrevdevRequestLogging`,
  `UseSolrevdevClientLogContext`, `UseSolrevdevUserLogContext` and
  `UseSolrevdevRequestLogging`.
