# Solrevdev.Logging.AspNetCore

Shared Serilog request-logging infrastructure for ASP.NET Core, extracted from three
internal applications that had each hand-copied and then diverged from the same code.

Install:

    dotnet add package solrevdev.Logging.AspNetCore

Wire it up. The order is not a style preference; each step below is load bearing.

    builder.Services.AddSolrevdevRequestLogging();

    app.UseForwardedHeaders();          // your policy, not this package's
    app.UseSolrevdevClientLogContext(); // ClientIp and TraceId, above anything that short-circuits
    app.UseSolrevdevRequestLogging();   // Serilog's one-line request summary
    app.UseStaticFiles();
    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseSolrevdevUserLogContext();   // UserId and UserEmail, in the one-line window
    app.UseAuthorization();

## What you get

- `ClientIp` rendered so a person can read it. A dual-stack socket delivers an IPv4 caller
  as `::ffff:203.0.113.42` and a local one as `::1`; both become the dotted form, so one
  filter finds one caller. All of 127.0.0.0/8 collapses to `127.0.0.1`, a link-local address
  keeps its zone suffix, and a public IPv6 address passes through untouched rather than being
  run through `MapToIPv4`, which would silently return a wrong but plausible IPv4 address.
- `ForwardedFor` reassembled from what the caller actually claimed. `UseForwardedHeaders`
  consumes the entries it accepts, so reading the header afterwards yields nothing or a
  leftover. This puts the claim back beside the settled address, which is what makes a
  mismatch visible.
- `Referer` with its query string removed. Password-reset and email-confirmation tokens ride
  in that query string, and a browser sends the page it came from as the next request's
  Referer.
- `UserId` and `UserEmail` on every event inside an authenticated request, pushed onto
  Serilog's LogContext so no call site changes. Anonymous requests carry neither property
  rather than carrying nulls, because an absent property is filterable and an empty one is
  not.
- Static files, `/healthcheck` and `/version` demoted to Verbose when they succeed, and left
  alone when they fail.
- `LoggingContract`, the property names as constants and as sets, so each application can
  test that its own request summaries still carry them.

## What it deliberately does not do

No rate limiting, no throttling, no `ForwardedHeadersOptions` policy, and nothing that keys
off an address for anything but a log property. Those are the consuming application's
decisions. The address this package renders is reshaped to be readable, which is exactly
wrong for a partition key: never use it for a rate limit, a throttle, a cache partition, an
authorisation decision or a persisted column. It reads identically to the raw address for
every caller arriving in native, non-mapped, non-loopback form, which is what lets that
mistake survive testing.

Full documentation: https://github.com/solrevdev/solrevdev.logging

MIT licensed.
