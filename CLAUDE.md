# CLAUDE.md

Context for Claude Code working on `solrevdev.logging`.

## What this is

A NuGet library, `solrevdev.Logging.AspNetCore`, holding the Serilog request-logging
infrastructure that three internal ASP.NET Core applications had each hand-copied and then
allowed to diverge. All three are `net10.0` and all three write into one shared Seq.

The package is the UNION of the three copies, not the smallest common part. Everything in it
was measured in one of those applications first.

## Commands

```bash
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack src/Solrevdev.Logging.AspNetCore --configuration Release
```

The SDK is pinned by `global.json` to 10.0.100 with `rollForward: latestFeature`, matching the
three consumers. Tests are xunit.v3 3.2.2 with xunit.runner.visualstudio 3.1.5 and
Microsoft.NET.Test.Sdk 18.9.0, which is what both sibling applications run.

## Layout

```
src/Solrevdev.Logging.AspNetCore/
  ClientAddress.cs                  address rendering, log properties ONLY
  ForwardedForHeader.cs             reassembles the caller's claim
  RefererHeader.cs                  strips the query string; a security control
  HeaderValue.cs                    internal; trims and caps
  RequestLogOptions.cs              the only things an application may vary
  RequestLogLevel.cs                the GetLevel function
  RequestSummaryEnricher.cs         the EnrichDiagnosticContext delegate
  ClientLogContextMiddleware.cs     ClientIp and TraceId, above the rate limiter
  UserLogContextMiddleware.cs       UserId and UserEmail, between auth and authz
  LoggingContract.cs                the property names, as constants and as sets
  *Extensions.cs                    registration
tests/Solrevdev.Logging.AspNetCore.Tests/
```

## Rules that bite

1. **This package must never grow rate limiting, throttling, or address-keyed
   partitioning.** No `ForwardedHeadersOptions` policy either. Which peers to trust and how to
   bucket a limiter are the consuming application's decisions and they are load bearing: a
   whole office leaves through one NAT address. If a task asks for one of these, the answer is
   that it belongs in the application, and say so rather than adding it quietly.

2. **`ClientAddress.Describe` is for log properties only.** Its output is reshaped to be
   readable, which makes it wrong for anything needing an address that is stable and unique
   per caller. For every routable address the reshaped and the raw string are IDENTICAL, which
   is exactly what lets this mistake survive testing and reach production. Never call it from
   a partition key, a cache key, an authorisation decision or a persisted column.

3. **Absent, never null.** Nothing here writes an empty string to keep a shape. An anonymous
   request carries no `UserId` and no `UserEmail` at all, and a request with no Referer carries
   no `Referer`. An absent property is filterable in Seq and a null one is not. Tested.

4. **The property names are not configurable, and that is the point.** Several applications
   write into one Seq. A property that means the same thing under two names is a filter that
   silently returns half the traffic. `LoggingContract` owns the names; nothing else may type
   one as a literal.

5. **The two-middleware split is not tidiness.** The user context can only sit between
   `UseAuthentication` and `UseAuthorization`, which is below the rate limiter, and a refused
   authentication POST short-circuits there. Before the split, a 429 logged with no address and
   no trace id. Do not merge the two middleware back together, and do not move either
   registration in a sample or a doc without saying why.

6. **Referer sanitising is a security control.** The `Uri.TryCreate` `file://` trap is
   unit-tested for the exact reason that a "simplification" of that method silently reinstates
   a live token in the log store. Do not rewrite `RefererHeader.Sanitise` without keeping that
   test green and keeping its comment.

7. **`::1` is not an IPv4-mapped address.** `IsIPv4MappedToIPv6` is `false` for it on .NET 10.
   The `IsLoopback` test is what catches it, and the test asserts the runtime fact as well as
   the behaviour so a future runtime change fails loudly.

8. **Every public member needs XML docs.** `GenerateDocumentationFile` plus
   `TreatWarningsAsErrors` means the build fails without them. Deliberate: this package exists
   to carry reasons between applications, and a member without its reason is the drift it was
   written to stop.

9. **Comments carry the measurement, not the conclusion.** The ones ported from the three
   applications record what was observed on a running site. Do not compress them into a
   restatement of the code.

## Commits

Conventional Commits, imperative subject, scope where it helps. Examples:
`fix: keep the loopback test above the mapped check`,
`docs(readme): record the trusted-publishing setup`.

## Publishing

`.github/workflows/publish.yml` uses NuGet trusted publishing over GitHub OIDC. There is no
`NUGET_API_KEY` secret in this repository and none should be added. The `Log in to NuGet` step
must stay immediately before the push, because the key it mints lasts one hour. The one-time
nuget.org policy setup is in the README under Publishing.

## Keeping the docs current

`README.md`, `PACKAGE.md` and this file describe the same package at three lengths. A change
to the public surface or to the required pipeline order changes all the ones it touches in the
same commit, or a reader cannot tell which is newer. `PACKAGE.md` is the one NuGet.org renders,
so it stays plain: no raw HTML, no collapsible sections.
