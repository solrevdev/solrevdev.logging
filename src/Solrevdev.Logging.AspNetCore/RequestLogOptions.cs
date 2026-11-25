using System.Security.Claims;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// What each application is allowed to vary about request logging.
/// </summary>
/// <remarks>
/// Deliberately small. The property NAMES are not here, because several applications write
/// into one shared log store and a renameable property is a filter that returns half the
/// traffic. What varies between applications is which claims carry the user, which paths are
/// not worth a line, and how much of a caller-supplied header to keep.
/// </remarks>
public sealed class RequestLogOptions
{
    /// <summary>
    /// The configuration section these options are conventionally bound from.
    /// </summary>
    public const string SectionName = "RequestLogging";

    /// <summary>
    /// The claim carrying the user's id. Defaults to
    /// <see cref="ClaimTypes.NameIdentifier"/>.
    /// </summary>
    public string UserIdClaimType { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>
    /// The claim carrying the user's email address. Defaults to <see cref="ClaimTypes.Email"/>.
    /// </summary>
    public string UserEmailClaimType { get; set; } = ClaimTypes.Email;

    /// <summary>
    /// Whether to fall back to <c>Identity.Name</c> when the email claim is missing. On by
    /// default.
    /// </summary>
    /// <remarks>
    /// The fallback exists for a measured case rather than for tidiness. A cookie lasting
    /// fourteen days that was issued BEFORE the Email claim existed still has to resolve to
    /// something, or every session issued before the change logs a null address until it
    /// expires. One of the sibling applications also puts the address in the Name claim and
    /// has never issued an Email claim at all.
    /// </remarks>
    public bool FallBackToIdentityNameForEmail { get; set; } = true;

    /// <summary>
    /// Overrides how the user's id is read. Null means read
    /// <see cref="UserIdClaimType"/>.
    /// </summary>
    public Func<ClaimsPrincipal, string?>? UserIdSelector { get; set; }

    /// <summary>
    /// Overrides how the user's email address is read. Null means read
    /// <see cref="UserEmailClaimType"/> and then, if
    /// <see cref="FallBackToIdentityNameForEmail"/> is set, <c>Identity.Name</c>.
    /// </summary>
    public Func<ClaimsPrincipal, string?>? UserEmailSelector { get; set; }

    /// <summary>
    /// Path prefixes whose SUCCESSFUL requests are demoted to Verbose. Defaults to
    /// <c>/healthcheck</c> and <c>/version</c>.
    /// </summary>
    /// <remarks>
    /// Matched with <c>StartsWithSegments</c>, so <c>/versionhistory</c> is not quietened by
    /// <c>/version</c>. A prefix match on the raw string would have been, which is the bug
    /// this avoids.
    /// </remarks>
    public IList<string> QuietPathPrefixes { get; } = ["/healthcheck", "/version"];

    /// <summary>
    /// File extensions that mean the response came from wwwroot rather than from a handler.
    /// </summary>
    /// <remarks>
    /// Matched on the extension rather than on a folder prefix, because assets are spread
    /// over css, js, lib, fonts and images plus icons and a manifest sitting at the root. No
    /// page or API route in these applications carries a file extension, so the test cannot
    /// quieten a real page by accident. Empty this set to switch static-file demotion off.
    /// </remarks>
    public ISet<string> QuietFileExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs", ".map",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".avif", ".bmp",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".webmanifest"
    };

    /// <summary>
    /// How much of the user agent to keep. Unbounded it would bloat every summary event.
    /// </summary>
    public int MaximumUserAgentLength { get; set; } = 512;

    /// <summary>
    /// How much of the sanitised referer to keep.
    /// </summary>
    public int MaximumRefererLength { get; set; } = RefererHeader.DefaultMaximumLength;

    /// <summary>
    /// How much of the reassembled <c>X-Forwarded-For</c> claim to keep.
    /// </summary>
    public int MaximumForwardedForLength { get; set; } = ForwardedForHeader.DefaultMaximumLength;

    /// <summary>
    /// Whether the request summary carries <see cref="LoggingContract.Navigation"/>. Off by
    /// default.
    /// </summary>
    /// <remarks>
    /// Off so that upgrading from 1.2.0 changes no application's output. See
    /// <see cref="RequestNavigation"/> for the values.
    /// </remarks>
    public bool IncludeNavigation { get; set; }

    /// <summary>
    /// Whether the request summary carries <see cref="LoggingContract.RequestQuery"/>. Off by
    /// default.
    /// </summary>
    /// <remarks>
    /// Off so that upgrading from 1.2.0 changes no application's output. Switched on with an
    /// empty <see cref="RequestQueryKeys"/>, every key is written as <c>key=(set)</c> or
    /// <c>key=</c>: the log shows which keys were sent and no value at all.
    /// </remarks>
    public bool IncludeRequestQuery { get; set; }

    /// <summary>
    /// Whether <see cref="LoggingContract.SpanId"/> is written beside
    /// <see cref="LoggingContract.TraceId"/>, on the request summary and on the client log
    /// context. Off by default.
    /// </summary>
    /// <remarks>
    /// Off so that upgrading from 1.2.0 changes no application's output.
    /// </remarks>
    public bool IncludeSpanId { get; set; }

    /// <summary>
    /// The query string keys whose values may be logged. Empty by default, and compared
    /// without regard to case.
    /// </summary>
    /// <remarks>
    /// List the keys that name a record, a page or a filter: an id, a page number, a sort
    /// order. Leave out anything a person types, such as a search box or a return URL. A
    /// listed key still has its value withheld when the value does not pass the safe-value
    /// rule in <see cref="RequestQuery.Describe(Microsoft.AspNetCore.Http.IQueryCollection, RequestLogOptions)"/>, so listing a key is
    /// permission to log an id, not free text.
    /// </remarks>
    public ISet<string> RequestQueryKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The most <c>key=value</c> pairs of the query string to keep. Defaults to
    /// <see cref="RequestQuery.DefaultMaximumPairs"/>.
    /// </summary>
    public int MaximumRequestQueryPairs { get; set; } = RequestQuery.DefaultMaximumPairs;

    /// <summary>
    /// The longest query string value that may be logged, for a listed key. A longer value is
    /// withheld, not cut. Defaults to <see cref="RequestQuery.DefaultMaximumValueLength"/>.
    /// </summary>
    public int MaximumRequestQueryValueLength { get; set; } = RequestQuery.DefaultMaximumValueLength;

    /// <summary>
    /// The application's own request summary fields, run after the package's, in the order
    /// added. Empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how an application adds fields without replacing
    /// <c>EnrichDiagnosticContext</c> and wiring the package's enricher and level function by
    /// hand, which also loses the startup guard.
    /// </para>
    /// <para>
    /// <strong>An enricher that throws is caught.</strong> The exception is written to
    /// Serilog's <c>SelfLog</c> and the next enricher still runs. The package's properties
    /// are set before any of these, so they are already on the summary. Letting it throw was
    /// measured against Serilog.AspNetCore 10.0.0 and is worse than it sounds: the request
    /// fails with the enricher's exception after the page has run, and no summary line is
    /// written for it at all.
    /// </para>
    /// <para>
    /// Do not set a <see cref="LoggingContract"/> name from here. The last write wins, so an
    /// enricher that does replaces the package's value.
    /// </para>
    /// </remarks>
    public IList<Action<Serilog.IDiagnosticContext, Microsoft.AspNetCore.Http.HttpContext>> SummaryEnrichers { get; } = [];

    /// <summary>
    /// Reads the user's id from a principal.
    /// </summary>
    /// <remarks>
    /// Blank is absent. See <see cref="ResolveUserEmail"/> for why, and for what "blank"
    /// covers.
    /// </remarks>
    /// <param name="principal">The principal the authentication middleware settled.</param>
    /// <returns>The id, or null when the claim is absent or carries no value.</returns>
    public string? ResolveUserId(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        return Blank(
            UserIdSelector is not null
                ? UserIdSelector(principal)
                : principal.FindFirst(UserIdClaimType)?.Value);
    }

    /// <summary>
    /// Reads the user's email address from a principal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blank is absent, and that rule is here rather than at the two call sites because there
    /// are two of them and they disagreed. A <c>NameIdentifier</c> claim holding an empty
    /// string is a real thing to find in a cookie, and it used to reach the log context as
    /// <c>UserId=""</c> on every event inside the request while the request summary, which
    /// tested emptiness rather than nullness, left the property off. One request, two
    /// answers, and the empty one is the one rule 3 exists to stop: a property nobody can
    /// filter on.
    /// </para>
    /// <para>
    /// Blank means null, empty, or whitespace only. Nothing else is touched: a value with
    /// content is passed through exactly as the claim or the selector gave it, spaces and all,
    /// because trimming an identifier is how you stop matching it. A selector returning an
    /// empty string is treated the same way as an empty claim, so switching to one cannot
    /// change what an absent user looks like.
    /// </para>
    /// <para>
    /// A blank email claim also falls through to <c>Identity.Name</c> when
    /// <see cref="FallBackToIdentityNameForEmail"/> is set, which is what the fallback was
    /// always for.
    /// </para>
    /// </remarks>
    /// <param name="principal">The principal the authentication middleware settled.</param>
    /// <returns>The address, or null when neither the claim nor the fallback has a value.</returns>
    public string? ResolveUserEmail(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        if (UserEmailSelector is not null)
        {
            return Blank(UserEmailSelector(principal));
        }

        var email = Blank(principal.FindFirst(UserEmailClaimType)?.Value);

        return email is not null || !FallBackToIdentityNameForEmail
            ? email
            : Blank(principal.Identity?.Name);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
