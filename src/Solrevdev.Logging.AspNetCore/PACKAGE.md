# Solrevdev.Logging.AspNetCore

Shared Serilog request logging for ASP.NET Core. Several applications write into one Seq
server, are told apart by an `Application` property, and use the same property names so one
query means the same thing across all of them.

Install:

    dotnet add package solrevdev.Logging.AspNetCore

## Set up

This package does not configure Serilog. Configure your own logger and include
`Enrich.FromLogContext()`, or the context middleware properties never reach your ordinary
application log events.

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.Services.AddSolrevdevRequestLogging();

`AddSolrevdevRequestLogging` is required. Each of the three `Use` calls below throws
`InvalidOperationException` at startup without it. Registering options is not enough on its
own: `IOptions<RequestLogOptions>` resolves to a default instance in any host, so an
application that forgot the call used to start and run on package defaults it never chose.

Then the middleware, in this order:

    app.UseForwardedHeaders();
    app.UseSolrevdevClientLogContext(); // ClientIp and TraceId, above anything that short-circuits
    app.UseSolrevdevRequestLogging();   // Serilog's request summary
    app.UseStaticFiles();
    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseSolrevdevUserLogContext();   // UserId and UserEmail
    app.UseAuthorization();

Behind a load balancer, the application must also enable and configure forwarded-header
processing itself. `app.UseForwardedHeaders()` alone reads nothing, because the default
`ForwardedHeadersOptions.ForwardedHeaders` is `None`. Set `ForwardedHeaders`, set a
`ForwardLimit` matching the number of hops, and list the peers or networks you trust in
`KnownProxies` or `KnownIPNetworks`.

## What you get

- `ClientIp` rendered so a person can read it. A dual-stack socket delivers an IPv4 caller as
  `::ffff:203.0.113.42` and a local one as `::1`, and both become the dotted form. All of
  127.0.0.0/8 collapses to `127.0.0.1`, a link-local address keeps its zone suffix, and a
  public IPv6 address passes through untouched rather than being run through `MapToIPv4`,
  which silently returns a wrong but plausible IPv4 address.
- `ForwardedFor`, a best-effort reconstruction of what the caller claimed.
  `UseForwardedHeaders` consumes the entries it accepts, so reading the header afterwards
  finds a leftover or nothing. This puts the settled address back beside it, which makes a
  difference visible. It is untrusted and lossy: ports, intermediate hops and the caller's own
  spelling do not survive. Use it as a clue, never for identity or access.
- `Referer` with its query string and fragment removed. Password-reset and email-confirmation
  tokens ride in query strings, and a browser sends the page it came from as the next
  request's Referer. This is that one control, not general secret redaction.
- `UserId` and `UserEmail` on every event inside an authenticated request, pushed onto
  Serilog's LogContext so no call site changes.
- Static files, `/healthcheck` and `/version` demoted to Verbose when they succeed, and left
  alone when they fail.
- `LoggingContract`, the property names as constants and as sets, so each application can test
  that its own request summaries still carry them.

Properties are omitted rather than set to an empty string. An anonymous request carries no
user properties, a request with no Referer carries no `Referer`, and a claim holding an empty
string counts as absent. `LoggingContract.OptionalProperties` lists the names a legitimate
request can arrive without, so a contract test asserts `RequiredProperties` minus those.

## What it deliberately does not do

No rate limiting, no throttling, no `ForwardedHeadersOptions` policy, and nothing that keys off
an address for any purpose other than a log property. Those are the consuming application's
decisions.

The address this package renders is reshaped to be readable, which is exactly wrong for a
partition key. Never use it for a rate limit, a throttle, a cache partition, an authorisation
decision or a stored column. It reads identically to the raw address for every caller arriving
in native, non-mapped, non-loopback form, which is what lets that mistake survive testing.

Full documentation: https://github.com/solrevdev/solrevdev.logging

MIT licensed.
