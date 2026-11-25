namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// A refusal already logged in this request, as <see cref="FormRefusalLogFilter"/> reads it.
/// </summary>
/// <remarks>
/// <para>
/// A form that comes back with errors is often the second half of a refusal something else
/// has already logged: a command pipeline, or a sign-in page. Two Warnings would count one
/// refusal twice. So whatever writes the first line records that here, and the filter writes
/// its own line at Information, naming the event it follows.
/// </para>
/// <para>
/// Read-only, because the filter only reads. An application whose refusal state lives in a
/// project that cannot reference this package registers a scoped adapter over its own type.
/// One that has no such state registers <see cref="LoggedRefusal"/> with
/// <see cref="RefusalLoggingExtensions.AddSolrevdevLoggedRefusal"/>.
/// </para>
/// <para>
/// The filter resolves this from the request services and does not need one. With none
/// registered every form refusal is a Warning with no
/// <see cref="LoggingContract.FollowsEvent"/>.
/// </para>
/// </remarks>
public interface ILoggedRefusal
{
    /// <summary>The refusal event last written in this request. Null when none was.</summary>
    string? LoggedEvent { get; }

    /// <summary>
    /// The request that refusal was for, such as a command name. Null when no such request
    /// was involved, as for a sign-in.
    /// </summary>
    string? LoggedRequestName { get; }

    /// <summary>
    /// The message the page shows for that refusal, where the log was given different text
    /// because the message holds a name or another party's words. Null when they are the same.
    /// </summary>
    string? ShownMessage { get; }

    /// <summary>The text the log was given in place of <see cref="ShownMessage"/>.</summary>
    string? LoggedMessage { get; }
}

/// <summary>
/// The default <see cref="ILoggedRefusal"/>: one per request, written by whatever logs a
/// refusal.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RefusalLoggingExtensions.AddSolrevdevLoggedRefusal"/> registers it under both
/// types, and in one request they are the same object. Inject <see cref="LoggedRefusal"/> to
/// write: call <see cref="Logged"/> straight after the log call. Inject
/// <see cref="ILoggedRefusal"/> to read, as the filter does.
/// </para>
/// <para>
/// Code that also runs where nothing registered it, such as a handler shared with a worker,
/// takes it as an optional constructor parameter, <c>LoggedRefusal? loggedRefusal = null</c>,
/// and calls <c>loggedRefusal?.Logged(...)</c>. The container passes null there.
/// </para>
/// <para>
/// It touches no ASP.NET type, on purpose. See "Considered, not done" in MAINTAINING.md.
/// </para>
/// </remarks>
public sealed class LoggedRefusal : ILoggedRefusal
{
    /// <inheritdoc />
    public string? LoggedEvent { get; private set; }

    /// <inheritdoc />
    public string? LoggedRequestName { get; private set; }

    /// <inheritdoc />
    public string? ShownMessage { get; private set; }

    /// <inheritdoc />
    public string? LoggedMessage { get; private set; }

    /// <summary>
    /// Records that a refusal was logged. A form redisplayed afterwards then names this event
    /// and is not a second Warning.
    /// </summary>
    /// <param name="eventName">The event written.</param>
    /// <param name="requestName">The request refused, if there was one.</param>
    /// <param name="shownMessage">The page's message, when the log was given other text.</param>
    /// <param name="loggedMessage">That other text.</param>
    public void Logged(
        string eventName,
        string? requestName = null,
        string? shownMessage = null,
        string? loggedMessage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        LoggedEvent = eventName;
        LoggedRequestName = requestName;
        ShownMessage = shownMessage;
        LoggedMessage = loggedMessage;
    }
}
