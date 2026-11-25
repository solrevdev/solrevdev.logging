using System.Reflection;

namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// The shape of an event name, and a check an application's own test can run over its
/// vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// Several applications write into one log store, and the useful question is almost never
/// "find me a line of text". It is "every order this week"
/// or "every refusal". A message template answers neither once somebody rewords it, so the
/// queryable handle is the <see cref="LoggingContract.Event"/> property with a fixed value.
/// </para>
/// <para>
/// The convention is <c>area.thing.outcome</c>: lowercase, exactly three parts, dots between
/// the parts and underscores allowed inside one. The outcome is past tense, because an event
/// has already happened, though no check can hold that. Put the name first in the template,
/// as <c>{Event}</c>, so it reads as a tag at the head of the line and
/// <c>Event = 'auth.signin.succeeded'</c> is the whole query.
/// </para>
/// <para>
/// Three parts exactly. "Two or more" would let <c>auth.signout</c> in beside
/// <c>auth.signin.succeeded</c>, and a vocabulary where some names have an outcome and some
/// do not is one you have to remember instead of guess.
/// </para>
/// <para>
/// A broken name fails silently: it never matches a query. So each application runs
/// <see cref="Violations"/> from a test of its own. The result is data and this package
/// references no test framework:
/// </para>
/// <code>
/// Assert.Empty(EventNames.Violations(typeof(MyEvents)));
/// </code>
/// </remarks>
public static class EventNames
{
    /// <summary>The number of dot-separated parts in an event name.</summary>
    public const int PartCount = 3;

    /// <summary>
    /// Whether a name is <c>area.thing.outcome</c>.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <returns>
    /// True when the name is exactly three parts joined by dots, each part starting with a
    /// lowercase ASCII letter and continuing with lowercase ASCII letters, digits or
    /// underscores.
    /// </returns>
    public static bool IsWellFormed(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var parts = name.Split('.');

        return parts.Length == PartCount && Array.TrueForAll(parts, IsPart);
    }

    /// <summary>
    /// Checks every public string constant on a type against the convention.
    /// </summary>
    /// <remarks>
    /// Reports a constant whose value is not well formed, and every constant that shares its
    /// value with another: two constants cannot mean the same event. Constants inherited from
    /// a base type are included.
    /// </remarks>
    /// <param name="vocabulary">The type holding the event names as public constants.</param>
    /// <param name="excludedConstants">
    /// Names of constants that are not event names, such as one holding the property name
    /// itself. Pass them with <c>nameof</c>.
    /// </param>
    /// <returns>
    /// The violations, ordered by constant name. Empty when the vocabulary holds.
    /// </returns>
    public static IReadOnlyList<EventNameViolation> Violations(Type vocabulary, params string[] excludedConstants)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(excludedConstants);

        var constants = vocabulary
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Where(field => !excludedConstants.Contains(field.Name, StringComparer.Ordinal))
            .Select(field => (field.Name, Value: (string?)field.GetRawConstantValue() ?? string.Empty))
            .ToList();

        var violations = new List<EventNameViolation>();

        foreach (var (name, value) in constants)
        {
            if (!IsWellFormed(value))
            {
                violations.Add(new EventNameViolation(name, value, EventNameViolationKind.Malformed));
            }
        }

        foreach (var group in constants.GroupBy(constant => constant.Value, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                violations.AddRange(group.Select(constant =>
                    new EventNameViolation(constant.Name, constant.Value, EventNameViolationKind.Duplicate)));
            }
        }

        return [.. violations.OrderBy(violation => violation.Constant, StringComparer.Ordinal)
            .ThenBy(violation => violation.Kind)];
    }

    private static bool IsPart(string part)
    {
        if (part.Length == 0 || !char.IsAsciiLetterLower(part[0]))
        {
            return false;
        }

        foreach (var character in part)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One way a vocabulary breaks the event-name convention.
/// </summary>
/// <param name="Constant">The name of the constant.</param>
/// <param name="Value">The constant's value.</param>
/// <param name="Kind">What is wrong with it.</param>
public sealed record EventNameViolation(string Constant, string Value, EventNameViolationKind Kind)
{
    /// <summary>
    /// The violation as a sentence, for a test's failure message.
    /// </summary>
    /// <returns>The constant, its value and what is wrong.</returns>
    public override string ToString() => Kind switch
    {
        EventNameViolationKind.Duplicate =>
            $"{Constant} is '{Value}', which another constant also uses. Two constants cannot mean the same event.",
        _ =>
            $"{Constant} is '{Value}'. Event names are lowercase area.thing.outcome, dots between parts and underscores inside one."
    };
}

/// <summary>
/// What is wrong with an event name.
/// </summary>
public enum EventNameViolationKind
{
    /// <summary>The value is not <c>area.thing.outcome</c>.</summary>
    Malformed = 0,

    /// <summary>Another constant on the same type has the same value.</summary>
    Duplicate = 1
}
