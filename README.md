# solrevdev.logging

Shared Serilog request-logging infrastructure for ASP.NET Core.

[![NuGet](https://img.shields.io/nuget/v/solrevdev.Logging.AspNetCore.svg)](https://www.nuget.org/packages/solrevdev.Logging.AspNetCore/)

## Why this exists

Three internal ASP.NET Core applications each carried a hand-copied version of the same
request-logging code, and the three copies had diverged. They wrote into one shared Seq and
were told apart by an `Application` property, so a divergence there is not cosmetic: a
property that means the same thing under two names is a filter that silently returns half the
traffic, and a property one application sets and another does not is a query that quietly
misses rows.

This package is the union of the three, not the smallest common part. Everything in it was
measured in one of them first, and the comments carry the measurement rather than the
conclusion.

## Install

```bash
dotnet add package solrevdev.Logging.AspNetCore
```

Targets `net10.0`. Depends on `Serilog.AspNetCore` 10.0.0 and the ASP.NET Core shared
framework.

## Wiring

```csharp
builder.Services.AddSolrevdevRequestLogging();

app.UseForwardedHeaders();          // your policy, not this package's
app.UseSolrevdevClientLogContext(); // ClientIp and TraceId
app.UseSolrevdevRequestLogging();   // Serilog's one-line request summary
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseSolrevdevUserLogContext();   // UserId and UserEmail
app.UseAuthorization();
```

**That order is the API.** Pin it with a test in your application, because every wrong
position fails silently:

| Move | What breaks |
| --- | --- |
| Client log context below the rate limiter | A refused authentication POST logs its 429 with no address and no trace id. That is the event somebody is most likely to go looking for, and it is the one that had nothing on it before the split |
| Client log context above `UseForwardedHeaders` | The address logged is the load balancer's, on every request |
| User log context above `UseAuthentication` | There is no principal to read, so nothing is ever set |
| User log context below `UseAuthorization` | An authorization refusal no longer names who was refused |
| Request logging below `UseStaticFiles` | Nothing breaks, but `RequestLogLevel` stops earning its place |

## What it sets

| Property | Where from |
| --- | --- |
| `ClientIp` | `Connection.RemoteIpAddress` after forwarded headers, rendered by `ClientAddress.Describe` |
| `TraceId` | `Activity.Current`, as an ordinary property so a file template can print it |
| `ForwardedFor` | The `X-Forwarded-For` the caller actually claimed, reassembled |
| `Referer` | Scheme, host and path only |
| `UserAgent` | Capped at 512 characters |
| `RequestHost`, `RequestScheme` | The request, after forwarded headers |
| `UserId`, `UserEmail` | The principal, only when authenticated |

`Application`, `Environment`, `MachineName`, `ProcessId` and `ThreadId` come from your
enrichers; `RequestMethod`, `RequestPath`, `StatusCode` and `Elapsed` come from Serilog's own
request logging. `LoggingContract` names all eighteen, split by who owns them.

### Absent, never null

An anonymous request carries no `UserId` and no `UserEmail` at all. An absent property is
filterable in Seq and a null one is not, so nothing here writes an empty string to keep a
shape. The same rule applies to every header: no Referer sent means no `Referer` property.

## The three traps this package exists to hold

**`::1` is not an IPv4-mapped address.** `IPAddress.Parse("::1").IsIPv4MappedToIPv6` is
`false` on .NET 10, measured, because mapping means the `::ffff:0:0/96` prefix and loopback is
its own address. The mapped check alone therefore leaves every local request reading `::1`
while the same machine over IPv4 reads `127.0.0.1`. The explicit `IPAddress.IsLoopback` test
is what catches it, and it covers all of 127.0.0.0/8, so `127.0.0.2` renders as `127.0.0.1`
too.

**`UseForwardedHeaders` consumes the header it accepts.** Send one `X-Forwarded-For` entry and
the middleware puts it on the connection and deletes the now-empty header, so reading the
header afterwards produces nothing at all. One entry is exactly what a single load balancer
sends, so the case that recorded nothing is the case production is in. `X-Original-For` is not
the missing half either: it holds the peer that was replaced, with a port on it. Its only use
is as a flag that the middleware acted, and the claim is recovered from the connection.

**`Uri.TryCreate` accepts a bare path as a `file://` URI.** Rebuilding from its parts then
escapes the query INTO the path rather than dropping it, so
`/auth/resetpassword?id=live-token` becomes `file:///auth/resetpassword%3Fid=live-token` and
the token survives. That is the one outcome referer sanitising exists to prevent, so only
`http` and `https` take the URI route and everything else is cut by hand at the first `?` or
`#`. It is unit-tested as a security control, not as tidying.

## What this package must never grow

No rate limiting. No throttling. No `ForwardedHeadersOptions` policy. Nothing that keys off an
address for anything but a log property.

Which peers to trust and how to bucket a limiter are the consuming application's decisions,
and they are load bearing: a whole office typically leaves through one NAT address. The
address `ClientAddress.Describe` returns is deliberately reshaped to be READABLE, which makes
it wrong for anything that has to be stable and unique per caller. Never use it for a rate
limit, a throttle, a cache partition, an authorisation decision or a persisted column. For
every routable address the reshaped and raw strings are identical, which is what makes the
mistake survive testing.

## Configuring it

`RequestLogOptions` is deliberately small. Property NAMES are not configurable, because that
is the divergence the package exists to end.

```csharp
builder.Services.AddSolrevdevRequestLogging(options =>
{
    options.UserIdClaimType = ClaimTypes.NameIdentifier;   // the default
    options.UserEmailClaimType = ClaimTypes.Email;         // the default
    options.FallBackToIdentityNameForEmail = true;         // the default

    // Or replace the reading entirely.
    options.UserEmailSelector = principal => principal.FindFirst("staff_email")?.Value;

    options.QuietPathPrefixes.Add("/metrics");
    options.MaximumUserAgentLength = 512;                  // the default
});
```

The email fallback to `Identity.Name` exists for a measured case rather than for tidiness. A
fourteen-day cookie issued BEFORE an application started emitting an Email claim still has to
resolve to something, or every session issued before that change logs a null address until it
expires. One of the three source applications also puts the address in the Name claim and has
never issued an Email claim at all.

The type is named `RequestLogOptions` rather than `RequestLoggingOptions` because
`Serilog.AspNetCore` already owns that second name in the same conceptual space.

## Testing your own application against the contract

```csharp
[Fact]
public void Every_request_summary_carries_the_contract()
{
    var summary = /* the request summary event your test host produced */;

    Assert.All(
        LoggingContract.RequiredProperties,
        name => Assert.Contains(name, summary.Properties.Keys));
}
```

`LoggingContract.EnricherProperties` is what this package sets and
`LoggingContract.HostProperties` is what Serilog and your enrichers set, so a failure names
which side is at fault. `LoggingContract.AuthenticatedProperties` must be present on a
signed-in request and absent on an anonymous one.

## Build and test

```bash
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack src/Solrevdev.Logging.AspNetCore --configuration Release
```

The SDK is pinned by `global.json` to 10.0.100 with `rollForward: latestFeature`, matching
the three consuming applications.

### Why these test packages

`xunit.v3` 3.2.2 with `xunit.runner.visualstudio` 3.1.5 and `Microsoft.NET.Test.Sdk` 18.9.0.
That is the combination both sibling applications run on the .NET 10 SDK today, and matching
them means a fix proved here can be pasted into either without a framework argument. The
xunit v4 packages move to Microsoft.Testing.Platform, which is a migration in its own right
and not one to make inside a library that three applications are about to take a dependency
on.

`TreatWarningsAsErrors` and `GenerateDocumentationFile` are both on, so a public member
shipped without XML docs fails the build. That is deliberate: this package exists to carry
hard-won reasons between three applications, and a member shipped without its reason is the
drift it was written to stop.

## Publishing

Releases go to nuget.org from `.github/workflows/publish.yml`, using **NuGet trusted
publishing**. There is no long-lived API key secret in this repository. The workflow requests
a short-lived GitHub OIDC token, nuget.org validates it against a policy registered on their
site, and hands back a temporary API key that is valid for one hour and one push. That is why
the `Log in to NuGet` step sits immediately before the push rather than at the top of the job,
and why the publish job carries `id-token: write` alongside the `contents: write` it needs for
the tag and the release. The pull-request `validate` job does not publish and does not get
`id-token: write`.

### One-time nuget.org setup

This cannot be scripted. On nuget.org, click the username, choose **Trusted Publishing**, and
add a policy:

| Field | Value |
| --- | --- |
| Package owner | `solrevdev` |
| Publisher | GitHub Actions |
| Repository owner | `solrevdev` |
| Repository | `solrevdev.logging` |
| Workflow file | `publish.yml` — the FILE NAME only, not the `.github/workflows/` path |
| Environment | leave empty; this workflow uses no GitHub environment |
| Package glob | `solrevdev.Logging.*` |

**Set the scope to allow pushing NEW packages, not only new versions.** This is the trap that
breaks a first release. A policy scoped "push only new package versions" cannot publish a
package that does not exist on nuget.org yet, and `solrevdev.Logging.AspNetCore` will be brand
new. Create the policy allowing new packages for the first release; it can be narrowed to
versions-only afterwards.

(If the repository were private, the policy would start only temporarily active for seven
days while nuget.org waits for a successful publish to supply the GitHub repository and owner
ids. This repository is public, so that does not apply.)

### Cutting a release

The `<Version>` in the csproj is the release number. A push to `master` publishes it as it
stands if it has no tag yet; once that version is tagged, a later push bumps the patch and a
`workflow_dispatch` chooses minor or major. The workflow refuses to publish if `origin/master`
moved under it, and a re-run against an existing tag is a safe retry.

## Where the code came from

| Piece | Source |
| --- | --- |
| `ClientAddress.Describe` | Two of the three, byte-identical in both |
| Forwarded-for reconstruction | Two of the three |
| Referer sanitising | The same two |
| `RequestLogLevel` | All three, merged |
| The two-middleware split | Two of the three, with the third's trace id |
| `LoggingContract` | New, replacing three sets of hand-typed constants |

## Contributing

Read `CLAUDE.md`. `AGENTS.md` points at the same rules for other agents.

## Licence

MIT. Copyright (c) solrevdev.
