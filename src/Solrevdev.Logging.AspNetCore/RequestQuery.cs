using Microsoft.AspNetCore.Http;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Cuts a query string down to an allowlist, for <see cref="LoggingContract.RequestQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ported from an application that had run it in production.
/// <see cref="LoggingContract.RequestPath"/> has no query string, so a page opened from a menu
/// and the same page opened to act on one record write the same line.
/// </para>
/// <para>
/// An allowlist, because a query string is whatever the caller typed: a search box, a return
/// URL, a reset token. A key that is not listed is written as <c>key=(set)</c>, so a key
/// missing from the list shows up in the log instead of vanishing. A listed key whose value
/// does not look like an id, a number, a date or an option name is written the same way, so
/// <c>?route=someone@example.com</c> leaks nothing.
/// </para>
/// <para>
/// An empty value is written <c>key=</c> whatever the key, because whether a search box held
/// anything is worth knowing and says nothing about what it held.
/// </para>
/// <para>
/// The allowlist is <see cref="RequestLogOptions.RequestQueryKeys"/> and it starts empty, so
/// until an application lists a key every value reads <c>(set)</c>. The request summary
/// carries the property only when <see cref="RequestLogOptions.IncludeRequestQuery"/> is set.
/// </para>
/// </remarks>
public static class RequestQuery
{
    /// <summary>Written in place of a value that was there but is not logged.</summary>
    public const string Withheld = "(set)";

    /// <summary>Written in place of a key that is not a plain name.</summary>
    public const string OtherKey = "(other)";

    /// <summary>Appended, after an <c>&amp;</c>, when pairs were left off the end.</summary>
    public const string More = "…";

    /// <summary>The default cap on the number of pairs kept.</summary>
    public const int DefaultMaximumPairs = 12;

    /// <summary>The default longest value that may be logged for a listed key.</summary>
    public const int DefaultMaximumValueLength = 50;

    /// <summary>
    /// The longest key written by name. A longer one is written as <see cref="OtherKey"/>.
    /// </summary>
    public const int MaximumKeyLength = 40;

    /// <summary>
    /// Describes a query string against an application's options.
    /// </summary>
    /// <param name="query">The request's query.</param>
    /// <param name="options">
    /// The options carrying the key allowlist, the pair cap and the value length.
    /// </param>
    /// <returns>The description, or null when there is nothing to write.</returns>
    public static string? Describe(IQueryCollection query, RequestLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Describe(
            query,
            options.RequestQueryKeys,
            options.MaximumRequestQueryPairs,
            options.MaximumRequestQueryValueLength);
    }

    /// <summary>
    /// Describes a query string against an explicit allowlist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules, in the order they apply to each pair:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// A key is caller text too. One that is not 1 to <see cref="MaximumKeyLength"/> ASCII
    /// letters, digits, <c>_</c>, <c>.</c> or <c>-</c> is written as <see cref="OtherKey"/>.
    /// </description></item>
    /// <item><description>
    /// An empty value, or a key sent with no value at all, is written <c>key=</c>.
    /// </description></item>
    /// <item><description>
    /// A value is written only when its key is in <paramref name="allowedKeys"/> and the value
    /// is 1 to <paramref name="maximumValueLength"/> ASCII letters, digits, <c>_</c>,
    /// <c>.</c>, <c>:</c> or <c>-</c>. That fits ids, numbers, dates, option names and
    /// references such as <c>ORD-000000012</c>. No space, no <c>@</c> and no slash, so no
    /// address, email or URL fits.
    /// </description></item>
    /// <item><description>
    /// Anything else is written <c>key=(set)</c>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Keys are sorted without regard to case, repeated pairs are written once, and the first
    /// <paramref name="maximumPairs"/> are kept. When more were left off, <c>&amp;…</c> is
    /// appended, so a cut list does not read as a whole one.
    /// </para>
    /// <para>
    /// The checks are written by hand and not as regular expressions ending in <c>$</c>,
    /// which is how the engine this was ported from did it. That anchor also matches before a
    /// final line feed, so a listed key's value ending in one was logged with it. Here such a
    /// value is withheld.
    /// </para>
    /// </remarks>
    /// <param name="query">The request's query.</param>
    /// <param name="allowedKeys">
    /// The keys whose values may be logged. Pass a set that ignores case, as
    /// <see cref="RequestLogOptions.RequestQueryKeys"/> does, or <c>PageNumber</c> and
    /// <c>pageNumber</c> are two keys.
    /// </param>
    /// <param name="maximumPairs">The most pairs to keep. Zero or less keeps nothing.</param>
    /// <param name="maximumValueLength">
    /// The longest value that may be logged. Zero or less withholds every value.
    /// </param>
    /// <returns>
    /// <c>key=value</c> pairs joined by <c>&amp;</c>, or null when there is no query or the
    /// pair cap keeps nothing.
    /// </returns>
    public static string? Describe(
        IQueryCollection query,
        ISet<string> allowedKeys,
        int maximumPairs = DefaultMaximumPairs,
        int maximumValueLength = DefaultMaximumValueLength)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(allowedKeys);

        if (query.Count == 0 || maximumPairs <= 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var (key, values) in query.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = IsSafeKey(key) ? key : OtherKey;
            if (values.Count == 0)
            {
                parts.Add(name + "=");
                continue;
            }

            var allowed = allowedKeys.Contains(key);
            foreach (var value in values)
            {
                parts.Add(
                    string.IsNullOrEmpty(value) ? name + "="
                    : allowed && IsSafeValue(value, maximumValueLength) ? $"{name}={value}"
                    : $"{name}={Withheld}");
            }
        }

        var shown = parts.Distinct(StringComparer.Ordinal).ToList();
        var described = string.Join('&', shown.Take(maximumPairs));

        return shown.Count > maximumPairs ? $"{described}&{More}" : described;
    }

    private static bool IsSafeKey(string key)
    {
        if (key.Length is 0 or > MaximumKeyLength)
        {
            return false;
        }

        foreach (var character in key)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeValue(string value, int maximumLength)
    {
        if (value.Length == 0 || value.Length > maximumLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.' or ':' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
