# AGENTS.md

Instructions for coding agents working on `solrevdev.logging`. This is the only rules file:
Codex reads it, and Claude Code 2.1.277 or later reads it when no `CLAUDE.md` exists. Do not
add a `CLAUDE.md`, including by running `/init`, because it would silently replace this file
for Claude Code.

## What this is

A NuGet library, `solrevdev.Logging.AspNetCore`, holding the Serilog request-logging
infrastructure that three internal ASP.NET Core applications had each hand-copied and then
allowed to diverge. The package targets `net10.0` and `net11.0`. All three applications are `net10.0` and all three write into one shared Seq.

The package is the UNION of the three copies, not the smallest common part. Everything in it
was measured in one of those applications first.

## Commands

```bash
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack src/Solrevdev.Logging.AspNetCore --configuration Release
```

Tests must pass before you commit.

.NET 10 is the LTS baseline. `global.json` asks for SDK 10.0.100 with `rollForward: latestMajor`
and no prereleases, so the newest installed SDK builds. The target list depends on that SDK: 11
or later builds `net10.0` and `net11.0`, SDK 10 alone builds `net10.0` only. The published
package must carry both, so CI installs SDKs 10.0.x and 11.0.x explicitly and checks the package
for both `lib/` folders. Do not switch the workflows back to `global-json-file`: setup-dotnet
installs nothing from a `latestMajor` global.json. The three consumers stay on `net10.0`, which
the package still serves. Tests are xunit.v3 3.2.2 with xunit.runner.visualstudio 3.1.5 and
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
   per caller. For every address that arrives in native, non-mapped, non-loopback form the
   reshaped and the raw string are IDENTICAL, which is exactly what lets this mistake survive
   testing and reach production. Note the precise scope: a mapped address is routable and does
   NOT render identically, so "for every routable address" would be wrong. Never call it from
   a partition key, a cache key, an authorisation decision or a persisted column. The
   authorisation item is in that list on purpose: an address-based gate is an ordinary thing
   to find in an application, and fed this value it is widened by the loopback collapse and
   moved by the mapped rewrite, failing open rather than loudly. The README documents a
   grep-count drift check that fails loudly when a third call site appears; keep it
   accurate.

3. **Absent, never blank.** Nothing here writes an empty string to keep a shape. An anonymous
   request carries no `UserId` and no `UserEmail` at all, and a request with no Referer carries
   no `Referer`. The convention is what keeps "the property is missing" and "the value was
   blank" from being the same row in the log store, so queries ask about absence. Tested.

   Blank means null, empty or whitespace only, and it is decided in ONE place per kind of
   value: `RequestLogOptions.ResolveUserId` and `ResolveUserEmail` for the user, and
   `HeaderValue.Truncate` for the headers. That is not tidiness either. The user rule used to
   live at both call sites and they disagreed: the log context tested for null and the request
   summary for emptiness, so an authenticated `NameIdentifier` claim holding an empty string
   logged `UserId=""` on every event inside the request and no property at all on the summary.
   A value that HAS content is never trimmed, because trimming an identifier is how you stop
   matching it.

   The header caps follow from the same rule. A cap of zero or less keeps nothing, so the
   helpers return null rather than throwing, and `AddSolrevdevRequestLogging` refuses such a
   cap at startup with `ValidateOnStart`. Before that, a negative cap reached a range slice and
   threw `ArgumentOutOfRangeException` out of a log enricher, mid-request, on whichever page
   was smoke-tested first. A cap is application configuration and never a caller's value, so
   startup is where it is worth failing.

4. **The property names are not configurable, and that is the point.** Several applications
   write into one Seq. A property that means the same thing under two names is a filter that
   returns half the traffic and looks like a quiet day. `LoggingContract` owns the names;
   nothing else may type one as a literal.

   The SETS in `LoggingContract` say which component owns a name. They do not say every request
   carries every name. `RequiredProperties` is the whole contract and `OptionalProperties` is
   the part of it a legitimate request can arrive without: caller-supplied headers, an address
   that needs a connection, a trace id that needs an `Activity`, and user properties that need
   both a principal and the claim. A consumer asserts `RequiredProperties` minus
   `OptionalProperties` unconditionally and the rest per scenario. Do not "fix" a contract test
   by writing an empty value, which is rule 3 traded away for a test's convenience. The
   published names and set semantics are used by three applications, so add a set rather than
   changing one.

5. **The two-middleware split is not tidiness.** The user context can only sit between
   `UseAuthentication` and `UseAuthorization`, which is below the rate limiter, and a refused
   authentication POST short-circuits there. Before the split, a 429 logged with no address and
   no trace id. Do not merge the two middleware back together, and do not move either
   registration in a sample or a doc without saying why.

6. **Referer sanitising is a security control.** The `Uri.TryCreate` `file://` trap is
   unit-tested for the exact reason that a "simplification" of that method silently reinstates
   a live token in the log store. Do not rewrite `RefererHeader.Sanitise` without keeping that
   test green and keeping its comment.

7. **`::1` is not an IPv4-mapped address.** `IsIPv4MappedToIPv6` is `false` for it on .NET 10,
   measured. The `IsLoopback` test is what catches it, and the test asserts the runtime fact as
   well as the behaviour so a future runtime change fails loudly.

8. **`MapToIPv4` fails silently, which is why the mapped guard exists.** On a genuine IPv6
   address it does NOT throw; it returns the last four bytes as a plausible, routable,
   completely wrong IPv4 address. Measured on .NET 10:
   `2001:db8:85a3::8a2e:370:7334` becomes `3.112.115.52`, and `::1` and `fe80::1` both become
   `0.0.0.1`. Never write that it throws. Without the guard every IPv6 caller would log as a
   fabricated address and nothing would raise.

9. **The mapped check runs BEFORE the loopback test and that order is behaviour.**
   `IPAddress.IsLoopback("::ffff:127.0.0.2")` is `false` raw and `true` after mapping, so
   testing loopback first sends that caller down the mapped branch instead and logs it as
   `127.0.0.2` rather than `127.0.0.1`. Measured, and pinned by a test that runs the swapped
   order rather than describing it, because an earlier README stated the swapped result as
   `::ffff:127.0.0.2`, which is what happens if you reason about the order instead of running
   it. That one input is the only test that discriminates the ordering; keep it.

10. **Every public member needs XML docs.** `GenerateDocumentationFile` plus
    `TreatWarningsAsErrors` means the build fails without them. Deliberate: this package exists
    to carry reasons between applications, and a member without its reason is the drift it was
    written to stop.

11. **Comments carry the measurement, not the conclusion.** The ones ported from the three
    applications record what was observed on a running site. Do not compress them into a
    restatement of the code.

12. **Addresses in examples, comments and tests come from documentation ranges only.**
    `2001:db8::/32`, `203.0.113.0/24`, `127.0.0.0/8` and `fe80::/10`. The repository is public
    and the XML doc file ships inside the package, so an address seen on real traffic is
    published as a disclosure of whoever it belongs to. `3.112.115.52` is the exception that
    looks like one: it is not an example but the measured output of `MapToIPv4` on the
    documentation address above it, so it changes only when that input does, and only after
    re-measuring.

13. **`ForwardedFor` is a best-effort reconstruction of untrusted information.** Not a copy of
    the header, and not evidence. `X-Original-For` is a HINT that the middleware acted, because
    any caller can send that header: an untrusted peer sending it and no `X-Forwarded-For` is
    reported as having forwarded itself. The reconstruction also loses the port, loses every
    consumed hop but the last, uses the framework's spelling of the address rather than the
    caller's characters, and assumes the default header names. `ClientIp` is unaffected by all
    of it, because it comes off the connection and this library never reads a forwarding header
    to decide an address. Describe a `ForwardedFor` and `ClientIp` mismatch as a reason to look,
    never as proof of spoofing, and do not hard-code a `ForwardLimit` here to hide the loss.

14. **The short overloads fall back to package defaults and say nothing.**
    `RequestLogLevel.For(context, exception)` and
    `RequestSummaryEnricher.EnrichRequestSummary` read `HttpContext.RequestServices` and use a
    default `RequestLogOptions` when there is nothing to read. Correct at runtime, and quiet in
    two places. A test built on a bare `DefaultHttpContext` has no `RequestServices` and
    measures the package defaults while appearing to measure the application; somebody
    integrating this package measured exactly that and concluded the overloads never consult DI
    at all. An application that wired the middleware without `AddSolrevdevRequestLogging` did
    the same thing at runtime.

    The second half is now caught at startup by `RequestLoggingMarkerService`, which the three
    `Use*` extensions check. **`IOptions` could not have caught it.** Every host calls
    `AddOptions`, so `IOptions<RequestLogOptions>` resolves to a default-constructed instance
    whether or not this package was registered. Measured against a running application on the
    published 1.0.0, which started, served requests, logged with `/healthcheck,/version` and a
    512 character cap, and threw nowhere. A private marker type is the only thing whose presence
    means "this package was added", so do not replace it with an options check. Do not describe
    the guard as covering the test case either, because no startup check can see it. Keep the
    request path free of the question: a level function runs on every request.

15. **The call-site count for `ClientAddress.Describe` is pinned.** Exactly two invocations in
    `src`, in `ClientLogContextMiddleware` and `RequestSummaryEnricher`. A third is how rule 2
    gets broken, so the README publishes the grep as a convention for consumers and the same
    grep applies here. If a change adds a call site, it needs a reason in the commit message
    and the expected count in the README moves in the same commit.

## Commits

Conventional Commits, imperative subject, scope where it helps. Examples:
`fix: keep the loopback test above the mapped check`,
`docs(readme): record the trusted-publishing setup`.

## Publishing

`.github/workflows/publish.yml` uses NuGet trusted publishing over GitHub OIDC. There is no
`NUGET_API_KEY` secret in this repository and none should be added. The `Log in to NuGet` step
must stay immediately before the push, because the key it mints lasts one hour. The one-time
nuget.org policy setup and the full release order are in `MAINTAINING.md`.

The git tag is pushed BEFORE the package. Nothing makes nuget.org and Git atomic, so the order
chooses which side dangles when a release does not complete: a tag with no package is a retry,
and a published version whose source nobody can identify is not. Do not reorder those two steps
back, and do not describe a second branch check as making them atomic.

## Keeping the docs current

`README.md`, `PACKAGE.md` and this file describe the same package at three lengths, and
`MAINTAINING.md` holds the release process, the test package choices and where the code came
from. A change to the public surface or to the required pipeline order changes all the ones it
touches in the same commit, or a reader cannot tell which is newer. `PACKAGE.md` is the one
NuGet.org renders, so it stays plain: no raw HTML, no collapsible sections.

The README is for somebody consuming the package. Keep maintainer material in
`MAINTAINING.md`, and keep the setup prerequisites in the README: a consumer needs
`Enrich.FromLogContext()` on their own logger and needs forwarded-header processing enabled and
configured, and neither is done by `AddSolrevdevRequestLogging`.
