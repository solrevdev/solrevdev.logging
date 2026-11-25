using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Logs a form post that came back to its page with errors, as
/// <see cref="RefusalEvents.WebFormRefused"/>: which page and handler, which fields, and what
/// the page said. Never what was typed.
/// </summary>
/// <remarks>
/// <para>
/// Ported from an application that had run it in production. A form post that fails
/// validation answers 200 with the form redisplayed, and without this nothing in the log
/// says why. A page filter sees every handler, so no page has to remember.
/// </para>
/// <para>
/// It fires on a POST whose handler returned the page with invalid model state. A page that
/// puts its message somewhere else and redirects writes no line.
/// </para>
/// <para>
/// When the refusal behind the errors is already logged, this line is Information and names
/// that event in <see cref="LoggingContract.FollowsEvent"/>, and its request in
/// <see cref="LoggingContract.FollowsRequest"/> where there was one. So
/// <c>@Level = 'Warning'</c> counts each refusal once and the two lines still read together.
/// It learns that from <see cref="ILoggedRefusal"/> and knows nothing of who wrote the first
/// line. When the page refused the form itself and logged nothing, or no
/// <see cref="ILoggedRefusal"/> is registered, this line is the only record, so it is the
/// Warning.
/// </para>
/// <para>
/// A model-binding message quotes the value that failed to bind, and another party's refusal
/// can echo an address. So every posted value of <see cref="MinimumRedactedLength"/> or more
/// characters is cut out of the messages before they are logged. Shorter values are left,
/// because cutting every "1" and "no" out of a sentence leaves nothing to read.
/// </para>
/// <para>
/// Add it with <see cref="RefusalLoggingExtensions.AddSolrevdevRefusalLogging"/>.
/// </para>
/// </remarks>
/// <param name="logger">The logger.</param>
public sealed class FormRefusalLogFilter(ILogger<FormRefusalLogFilter> logger) : IAsyncPageFilter
{
    /// <summary>
    /// The <see cref="LoggingContract.PageHandler"/> for a post to a page's unnamed handler,
    /// <c>OnPost</c>. Blank, it printed as a double space.
    /// </summary>
    public const string DefaultHandler = "(default)";

    /// <summary>
    /// The <see cref="LoggingContract.InvalidFields"/> entry for an error on the page as a
    /// whole and not on one field.
    /// </summary>
    public const string FormField = "(form)";

    /// <summary>The shortest posted value that is cut out of a message.</summary>
    public const int MinimumRedactedLength = 3;

    /// <summary>The most characters of one message kept.</summary>
    public const int MaximumMessageLength = 300;

    /// <summary>The most messages kept.</summary>
    public const int MaximumMessages = 20;

    // Written where a posted value was cut out, and after a message cut to length.
    private const string Cut = "…";

    private const string UnreadableValue = "The value could not be read.";

    // Templates are built from the contract constants, so no name is typed twice.
    private const string Head =
        "{" + LoggingContract.Event + "} {" + LoggingContract.PageName + "} {" + LoggingContract.PageHandler + "} redisplayed";

    private const string Tail =
        ", {" + LoggingContract.InvalidFields + "}: {" + LoggingContract.FormErrors + "}";

    private const string Alone = Head + Tail;

    private const string AfterEvent = Head + " after {" + LoggingContract.FollowsEvent + "}" + Tail;

    private const string AfterEventAndRequest =
        Head + " after {" + LoggingContract.FollowsEvent + "} {" + LoggingContract.FollowsRequest + "}" + Tail;

    private readonly ILogger<FormRefusalLogFilter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Does nothing. The filter acts after the handler has run.</summary>
    /// <param name="context">The selected handler.</param>
    /// <returns>A completed task.</returns>
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    /// <summary>
    /// Runs the handler, then writes the event when it returned the page with errors.
    /// </summary>
    /// <param name="context">The handler about to run.</param>
    /// <param name="next">The handler and the filters inside this one.</param>
    /// <returns>A task that completes when the handler has run.</returns>
    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        var executed = await next().ConfigureAwait(false);

        var request = executed.HttpContext.Request;
        if (!HttpMethods.IsPost(request.Method)
            || executed.Exception is not null
            || executed.Result is not PageResult
            || executed.ModelState.IsValid)
        {
            return;
        }

        var invalid = executed.ModelState
            .Where(entry => entry.Value is { ValidationState: ModelValidationState.Invalid })
            .ToList();
        if (invalid.Count == 0)
        {
            return;
        }

        var fields = invalid
            .Select(entry => entry.Key.Length == 0 ? FormField : entry.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var posted = PostedValues(request);
        var refusal = executed.HttpContext.RequestServices?.GetService<ILoggedRefusal>();
        var messages = invalid
            .SelectMany(entry => entry.Value!.Errors)
            .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? UnreadableValue : error.ErrorMessage)
            .Select(message => Redact(AsLogged(message, refusal), posted))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumMessages)
            .ToList();

        var page = executed.ActionDescriptor.ViewEnginePath;
        var handler = executed.HandlerMethod?.Name is { Length: > 0 } name ? name : DefaultHandler;

        if (refusal?.LoggedEvent is { Length: > 0 } followsEvent)
        {
            // Absent, never blank: no FollowsRequest when the first line had no request.
            if (refusal.LoggedRequestName is { Length: > 0 } followsRequest)
            {
                _logger.LogInformation(
                    AfterEventAndRequest,
                    RefusalEvents.WebFormRefused, page, handler, followsEvent, followsRequest, fields, messages);
            }
            else
            {
                _logger.LogInformation(
                    AfterEvent,
                    RefusalEvents.WebFormRefused, page, handler, followsEvent, fields, messages);
            }

            return;
        }

        _logger.LogWarning(Alone, RefusalEvents.WebFormRefused, page, handler, fields, messages);
    }

    /// <summary>
    /// A refusal can show the page a message the log was given other text for, because it
    /// holds a name or another party's words. The page copies that message into its form
    /// errors, so it is swapped here or it would reach the log by this line instead.
    /// </summary>
    private static string AsLogged(string message, ILoggedRefusal? refusal) =>
        refusal is { ShownMessage: { Length: > 0 } shown, LoggedMessage: { } logged } && shown != logged
            ? message.Replace(shown, logged, StringComparison.Ordinal)
            : message;

    private static List<string> PostedValues(HttpRequest request)
    {
        if (!request.HasFormContentType)
        {
            return [];
        }

        // Longest first, so a value containing a shorter one is cut whole.
        return request.Form
            .SelectMany(field => field.Value)
            .OfType<string>()
            .Select(value => value.Trim())
            .Where(value => value.Length >= MinimumRedactedLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToList();
    }

    private static string Redact(string message, List<string> posted)
    {
        foreach (var value in posted)
        {
            message = message.Replace(value, Cut, StringComparison.OrdinalIgnoreCase);
        }

        return message.Length <= MaximumMessageLength
            ? message
            : string.Concat(message.AsSpan(0, MaximumMessageLength), Cut);
    }
}
