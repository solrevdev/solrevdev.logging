namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Caps caller-supplied header values so one request cannot write an unbounded log event.
/// </summary>
internal static class HeaderValue
{
    /// <summary>
    /// Trims and caps a header value.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <param name="maximumLength">The most characters to keep.</param>
    /// <returns>The trimmed value cut to length, or null when it was empty or whitespace.</returns>
    internal static string? Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}
