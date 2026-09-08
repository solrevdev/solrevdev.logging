using System.Collections.Frozen;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// The property names a request summary is expected to carry, as constants and as sets.
/// </summary>
/// <remarks>
/// <para>
/// Several applications write into one shared log store and are told apart by
/// <see cref="Application"/>. A property that means the same thing under two names is a
/// filter that silently returns half the traffic, so the names are fixed here rather than
/// typed at each call site.
/// </para>
/// <para>
/// The sets exist so each consuming application can write one test asserting its own request
/// summaries carry exactly this contract. That test is the thing that catches an enricher
/// quietly dropping a property, which otherwise looks like a quiet day.
/// </para>
/// <para>
/// Ownership is split, and the split is why <see cref="EnricherProperties"/> and
/// <see cref="HostProperties"/> are separate sets:
/// <see cref="RequestSummaryEnricher"/> sets the first group, and Serilog's request logging
/// plus the configured enrichers set the second. A consuming application can only assert the
/// second group against a real request through its own host.
/// </para>
/// </remarks>
public static class LoggingContract
{
    /// <summary>Which application wrote the event. From <c>Serilog:Properties</c>.</summary>
    public const string Application = "Application";

    /// <summary>The runtime environment name. Set from the host, never from a literal.</summary>
    public const string Environment = "Environment";

    /// <summary>The machine the process is on. From the MachineName enricher.</summary>
    public const string MachineName = "MachineName";

    /// <summary>The process id. From the Process enricher.</summary>
    public const string ProcessId = "ProcessId";

    /// <summary>The managed thread id. From the Thread enricher.</summary>
    public const string ThreadId = "ThreadId";

    /// <summary>
    /// The request's W3C trace id, as an ordinary property.
    /// </summary>
    /// <remarks>
    /// Serilog carries a trace id of its own, but as a built-in field rather than a property,
    /// so no output template can render it and no query can filter on it by name. This is the
    /// same id as a property, which is what puts it in a log file and what lets a queued row
    /// be matched back to the request that queued it.
    /// </remarks>
    public const string TraceId = "TraceId";

    /// <summary>The HTTP method. Set by Serilog's request logging.</summary>
    public const string RequestMethod = "RequestMethod";

    /// <summary>The request path. Set by Serilog's request logging.</summary>
    public const string RequestPath = "RequestPath";

    /// <summary>The Host header the request arrived on.</summary>
    public const string RequestHost = "RequestHost";

    /// <summary>The scheme, after forwarded headers have been applied.</summary>
    public const string RequestScheme = "RequestScheme";

    /// <summary>The response status code. Set by Serilog's request logging.</summary>
    public const string StatusCode = "StatusCode";

    /// <summary>How long the request took, in milliseconds. Set by Serilog's request logging.</summary>
    public const string Elapsed = "Elapsed";

    /// <summary>
    /// The caller's address as the framework settled it, rendered by
    /// <see cref="ClientAddress.Describe"/>.
    /// </summary>
    public const string ClientIp = "ClientIp";

    /// <summary>
    /// The <c>X-Forwarded-For</c> the caller claimed, reassembled by
    /// <see cref="ForwardedForHeader.Claimed"/>.
    /// </summary>
    public const string ForwardedFor = "ForwardedFor";

    /// <summary>The caller's user agent, capped.</summary>
    public const string UserAgent = "UserAgent";

    /// <summary>The referring page with its query string removed. See <see cref="RefererHeader"/>.</summary>
    public const string Referer = "Referer";

    /// <summary>The signed-in user's id. Absent on an anonymous request, never null.</summary>
    public const string UserId = "UserId";

    /// <summary>The signed-in user's email address. Absent on an anonymous request, never null.</summary>
    public const string UserEmail = "UserEmail";

    /// <summary>
    /// The properties this library's enricher owns and sets on every request, signed in or
    /// not.
    /// </summary>
    /// <remarks>
    /// <see cref="ForwardedFor"/> and <see cref="Referer"/> are in this set because a request
    /// through a load balancer from a real browser carries both. Neither is invented when the
    /// header was not sent: an absent property is filterable and an empty one is not.
    /// </remarks>
    public static readonly IReadOnlySet<string> EnricherProperties = FrozenSet.ToFrozenSet(
    [
        ClientIp,
        ForwardedFor,
        Referer,
        UserAgent,
        RequestHost,
        RequestScheme,
        TraceId
    ],
    StringComparer.Ordinal);

    /// <summary>
    /// The properties the host owns: Serilog's own request-summary fields and the configured
    /// enrichers.
    /// </summary>
    public static readonly IReadOnlySet<string> HostProperties = FrozenSet.ToFrozenSet(
    [
        Application,
        Environment,
        MachineName,
        ProcessId,
        ThreadId,
        RequestMethod,
        RequestPath,
        StatusCode,
        Elapsed
    ],
    StringComparer.Ordinal);

    /// <summary>
    /// Every property a request summary must carry, whether or not anybody is signed in.
    /// </summary>
    public static readonly IReadOnlySet<string> RequiredProperties = FrozenSet.ToFrozenSet(
        [.. EnricherProperties, .. HostProperties],
        StringComparer.Ordinal);

    /// <summary>
    /// The properties a request summary must carry only when the request was authenticated,
    /// and must NOT carry when it was not.
    /// </summary>
    public static readonly IReadOnlySet<string> AuthenticatedProperties = FrozenSet.ToFrozenSet(
        [UserId, UserEmail],
        StringComparer.Ordinal);

    /// <summary>
    /// Everything in <see cref="RequiredProperties"/> and
    /// <see cref="AuthenticatedProperties"/> together.
    /// </summary>
    public static readonly IReadOnlySet<string> AllProperties = FrozenSet.ToFrozenSet(
        [.. RequiredProperties, .. AuthenticatedProperties],
        StringComparer.Ordinal);
}
