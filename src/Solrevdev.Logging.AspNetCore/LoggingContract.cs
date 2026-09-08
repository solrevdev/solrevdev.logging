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
/// The sets exist so each consuming application can test its own request summaries against
/// this contract. Such a test is the thing that catches an enricher quietly dropping a
/// property, which otherwise looks like a quiet day. Assert presence against
/// <see cref="RequiredProperties"/> minus <see cref="OptionalProperties"/>, and put the
/// optional names in the scenarios that actually produce them; the sets are about which
/// component owns a name, not about what one request happens to carry.
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
    /// A best-effort reconstruction of the caller's <c>X-Forwarded-For</c>, by
    /// <see cref="ForwardedForHeader.Claimed"/>. Untrusted, lossy, and for reading only.
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
    /// The properties this library's enricher OWNS, signed in or not. Ownership, not
    /// presence: see <see cref="OptionalProperties"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ForwardedFor"/> and <see cref="Referer"/> are in this set because a request
    /// through a load balancer from a real browser carries both, and because this is the set
    /// that answers "who would have set this". Neither is invented when the header was not
    /// sent: an absent property is filterable and an empty one is not.
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
    /// Every property a request summary can carry whether or not anybody is signed in:
    /// <see cref="EnricherProperties"/> and <see cref="HostProperties"/> together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>"Required" names the contract, not a guarantee about one request.</strong> The
    /// name is kept because three applications assert against it, but read it as "these are
    /// the names a request summary is made of, and nothing here may be renamed". Several of
    /// them are legitimately absent from a perfectly ordinary request, which is what
    /// <see cref="OptionalProperties"/> lists.
    /// </para>
    /// <para>
    /// So the useful unconditional assertion is this set MINUS
    /// <see cref="OptionalProperties"/>, and anything in the optional list is asserted per
    /// scenario instead: a browser request has a <see cref="UserAgent"/>, a request behind the
    /// balancer has a <see cref="ClientIp"/>, a health probe has neither a
    /// <see cref="Referer"/> nor a user. Requiring the whole set of every request fails on
    /// traffic that is doing nothing wrong, and the usual response to a test like that is to
    /// start writing empty values to satisfy it, which is rule 3 lost for a test's
    /// convenience.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> RequiredProperties = FrozenSet.ToFrozenSet(
        [.. EnricherProperties, .. HostProperties],
        StringComparer.Ordinal);

    /// <summary>
    /// The properties a request summary can carry only when the request was authenticated, and
    /// must NOT carry when it was not.
    /// </summary>
    /// <remarks>
    /// The "must not" half is unconditional and tested. The other half is not: an
    /// authenticated principal can be missing either claim, so both names are in
    /// <see cref="OptionalProperties"/> as well. An old cookie with no Email claim is the
    /// ordinary case and the reason
    /// <see cref="RequestLogOptions.FallBackToIdentityNameForEmail"/> exists.
    /// </remarks>
    public static readonly IReadOnlySet<string> AuthenticatedProperties = FrozenSet.ToFrozenSet(
        [UserId, UserEmail],
        StringComparer.Ordinal);

    /// <summary>
    /// The properties a legitimate request can arrive without, so a contract test must not
    /// require them unconditionally.
    /// </summary>
    /// <remarks>
    /// Each one is absent for a reason worth knowing rather than through a fault:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="Referer"/>, <see cref="UserAgent"/> and <see cref="ForwardedFor"/> are
    /// caller-supplied headers. An API client, a health probe and a direct request to the
    /// origin each send fewer of them than a browser behind the balancer does.
    /// </description></item>
    /// <item><description>
    /// <see cref="ClientIp"/> needs a connection. A request built in a test has no socket
    /// under it, and <c>DefaultHttpContext</c> leaves <c>RemoteIpAddress</c> null.
    /// </description></item>
    /// <item><description>
    /// <see cref="TraceId"/> needs an <see cref="System.Diagnostics.Activity"/>. There is one
    /// under an ASP.NET Core host and there is not one in a plain unit test, or when
    /// activity tracking has been switched off.
    /// </description></item>
    /// <item><description>
    /// <see cref="UserId"/> and <see cref="UserEmail"/> need both an authenticated principal
    /// and the claim. Either can be missing from a cookie that is still valid.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlySet<string> OptionalProperties = FrozenSet.ToFrozenSet(
        [ClientIp, TraceId, ForwardedFor, Referer, UserAgent, UserId, UserEmail],
        StringComparer.Ordinal);

    /// <summary>
    /// Everything in <see cref="RequiredProperties"/> and
    /// <see cref="AuthenticatedProperties"/> together.
    /// </summary>
    public static readonly IReadOnlySet<string> AllProperties = FrozenSet.ToFrozenSet(
        [.. RequiredProperties, .. AuthenticatedProperties],
        StringComparer.Ordinal);
}
