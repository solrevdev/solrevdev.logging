namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// The two event names the refusal filters write into <see cref="LoggingContract.Event"/>.
/// </summary>
/// <remarks>
/// The package owns these two names because it writes them, and three applications query them
/// in one log store. Both follow the <see cref="EventNames"/> shape. An application's own
/// vocabulary must not reuse either value.
/// </remarks>
public static class RefusalEvents
{
    /// <summary>
    /// A form post failed its anti-forgery check. Written by
    /// <see cref="AntiforgeryRefusalLogFilter"/>.
    /// </summary>
    public const string WebAntiforgeryRefused = "web.antiforgery.refused";

    /// <summary>
    /// A form post came back to its page with errors. Written by
    /// <see cref="FormRefusalLogFilter"/>.
    /// </summary>
    public const string WebFormRefused = "web.form.refused";
}
