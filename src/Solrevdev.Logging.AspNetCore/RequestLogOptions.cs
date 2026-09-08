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
    /// Reads the user's id from a principal.
    /// </summary>
    /// <param name="principal">The principal the authentication middleware settled.</param>
    /// <returns>The id, or null when the claim is absent.</returns>
    public string? ResolveUserId(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        return UserIdSelector is not null
            ? UserIdSelector(principal)
            : principal.FindFirst(UserIdClaimType)?.Value;
    }

    /// <summary>
    /// Reads the user's email address from a principal.
    /// </summary>
    /// <param name="principal">The principal the authentication middleware settled.</param>
    /// <returns>The address, or null when neither the claim nor the fallback is present.</returns>
    public string? ResolveUserEmail(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        if (UserEmailSelector is not null)
        {
            return UserEmailSelector(principal);
        }

        var email = principal.FindFirst(UserEmailClaimType)?.Value;

        return !string.IsNullOrEmpty(email) || !FallBackToIdentityNameForEmail
            ? email
            : principal.Identity?.Name;
    }
}
