namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Caps caller-supplied header values so one request cannot write an unbounded log event.
/// </summary>
/// <remarks>
/// <strong>Internal.</strong> It is documented at this length because the rule it enforces is
/// shared, not because a consumer can call it: the public way in is
/// <see cref="RefererHeader.Sanitise"/> or <see cref="ForwardedForHeader.Claimed"/>, and both
/// take the same cap and inherit the behaviour described below. The tests reach it through
/// <c>InternalsVisibleTo</c>.
/// </remarks>
internal static class HeaderValue
{
    /// <summary>
    /// Trims and caps a header value.
    /// </summary>
    /// <remarks>
    /// A cap of zero or less returns null rather than throwing. Keeping zero characters and
    /// keeping no property are the same outcome under rule 3, and a misconfigured cap must not
    /// be able to fail a request from inside a log enricher: an earlier version reached
    /// <c>trimmed[..maximumLength]</c> with a negative cap and threw
    /// <see cref="ArgumentOutOfRangeException"/> mid-request. The configuration mistake itself
    /// is caught at startup by the validation in
    /// <see cref="ServiceCollectionExtensions.AddSolrevdevRequestLogging(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>,
    /// which is where it is useful; this guard is the floor under the public helpers, which
    /// take a cap as a plain argument and can be called without the options at all.
    /// </remarks>
    /// <param name="value">The header value.</param>
    /// <param name="maximumLength">The most characters to keep.</param>
    /// <returns>
    /// The trimmed value cut to length, or null when it was empty or whitespace or when the
    /// cap keeps nothing.
    /// </returns>
    internal static string? Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maximumLength <= 0)
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}
