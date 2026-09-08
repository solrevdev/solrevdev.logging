# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
