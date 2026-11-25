using Microsoft.AspNetCore.Http;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Reduces the browser's <c>Sec-Fetch-*</c> headers to one word saying how a request was
/// started, for <see cref="LoggingContract.Navigation"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ported from an application that had run it in production. A session read back from the
/// log cannot otherwise tell a page a person clicked to from one opened from the address bar
/// or by a tool.
/// <see cref="LoggingContract.Referer"/> says where a request came from when one was sent. It
/// cannot say a request was not a click.
/// </para>
/// <para>
/// The words, in the order they are decided:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="Fetch"/>: not a page load, but a script on the page asking.</description></item>
/// <item><description><see cref="Frame"/>: a page loaded inside another one.</description></item>
/// <item><description><see cref="Direct"/>: a page opened with no page behind it: typed, pasted, a bookmark, or a browser driven by a tool.</description></item>
/// <item><description><see cref="CrossSite"/>: a link or form on another site.</description></item>
/// <item><description><see cref="User"/>: a click, key press or form submit on one of this site's pages, and the redirect that follows it.</description></item>
/// <item><description><see cref="Scripted"/>: a page load from this site that no click started, such as a script changing the location.</description></item>
/// </list>
/// <para>
/// Absent when the browser sent none of the headers, which is what curl, a test client and an
/// older browser do. The headers are the browser's word, so a tool can send any of them. This
/// sorts honest traffic and proves nothing about the rest.
/// </para>
/// <para>
/// The request summary carries it only when
/// <see cref="RequestLogOptions.IncludeNavigation"/> is set.
/// </para>
/// </remarks>
public static class RequestNavigation
{
    /// <summary>A script on the page asked, so this is not a page load.</summary>
    public const string Fetch = "fetch";

    /// <summary>A page loaded inside another one.</summary>
    public const string Frame = "frame";

    /// <summary>A page opened with no page behind it.</summary>
    public const string Direct = "direct";

    /// <summary>A link or form on another site.</summary>
    public const string CrossSite = "cross-site";

    /// <summary>A click, key press or form submit on one of this site's pages.</summary>
    public const string User = "user";

    /// <summary>A page load from this site that no click started.</summary>
    public const string Scripted = "scripted";

    private const string ModeHeader = "Sec-Fetch-Mode";
    private const string SiteHeader = "Sec-Fetch-Site";
    private const string DestHeader = "Sec-Fetch-Dest";
    private const string UserHeader = "Sec-Fetch-User";

    /// <summary>
    /// The one word for a request's <c>Sec-Fetch-*</c> headers.
    /// </summary>
    /// <param name="headers">The request headers.</param>
    /// <returns>
    /// One of the six constants on this class, or null when the caller sent none of
    /// <c>Sec-Fetch-Mode</c>, <c>Sec-Fetch-Site</c> and <c>Sec-Fetch-Dest</c>.
    /// </returns>
    public static string? Describe(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var mode = headers[ModeHeader].ToString();
        var site = headers[SiteHeader].ToString();
        var dest = headers[DestHeader].ToString();
        var user = headers[UserHeader].ToString();

        if (mode.Length == 0 && site.Length == 0 && dest.Length == 0)
        {
            return null;
        }

        if (!Is(mode, "navigate"))
        {
            return Fetch;
        }

        if (dest.Length > 0 && !Is(dest, "document"))
        {
            return Frame;
        }

        if (Is(site, "none"))
        {
            return Direct;
        }

        if (Is(site, "cross-site"))
        {
            return CrossSite;
        }

        return Is(user, "?1") ? User : Scripted;
    }

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
