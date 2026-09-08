namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Reduces a referer to scheme, host and path, dropping the query string and the fragment.
/// </summary>
/// <remarks>
/// <strong>This is a security control, not tidying.</strong> A password-reset link and an
/// email-confirmation link both carry their single-use token in the query string, and a
/// browser sends the page it came from as the Referer of the next request. Logging a referer
/// whole therefore copies live credentials into the log store, where they sit for the whole
/// retention period and are readable by everyone who can open it. Scheme, host and path
/// answer "where did this person come from" and carry nothing secret.
/// </remarks>
public static class RefererHeader
{
    /// <summary>
    /// The default cap on the sanitised value, in characters.
    /// </summary>
    public const int DefaultMaximumLength = 512;

    /// <summary>
    /// Strips the query string and fragment off a referer and caps what is left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scheme test is load bearing, and it is not defensive tidiness. Measured on a Unix
    /// host: <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> ACCEPTS
    /// <c>"/auth/resetpassword?id=live-token"</c> as an absolute <c>file://</c> URI, and
    /// rebuilding from its parts then yields
    /// <c>"file:///auth/resetpassword%3Fid=live-token"</c> with the query ESCAPED INTO THE
    /// PATH rather than removed. The token survives, which is the one outcome this method
    /// exists to prevent. A protocol-relative <c>"//host/x?id=token"</c> does the same thing.
    /// Only http and https take the URI route; everything else is cut by hand.
    /// </para>
    /// <para>
    /// Never throws. A referer is a request header, so it is whatever the caller typed, and a
    /// malformed one must not be able to fail a request. A value that is not a valid absolute
    /// http or https URI is still cut at the first <c>?</c> or <c>#</c> rather than kept
    /// whole: the reason for stripping the query is the token in it, and a malformed URL can
    /// carry a token just as well.
    /// </para>
    /// </remarks>
    /// <param name="referer">The raw header value, which may be absent or not a URI at all.</param>
    /// <param name="maximumLength">The most characters to keep.</param>
    /// <returns>Scheme, host and path, capped, or null when there was nothing worth keeping.</returns>
    public static string? Sanitise(string? referer, int maximumLength = DefaultMaximumLength)
    {
        if (string.IsNullOrWhiteSpace(referer))
        {
            return null;
        }

        var value = referer.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            value = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        }
        else
        {
            var delimiter = value.IndexOfAny(['?', '#']);
            if (delimiter >= 0)
            {
                value = value[..delimiter];
            }
        }

        return HeaderValue.Truncate(value, maximumLength);
    }
}
