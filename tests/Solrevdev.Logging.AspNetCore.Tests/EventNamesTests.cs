namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Holds the helper each application's own vocabulary test calls.
/// </summary>
public class EventNamesTests
{
    private static class GoodVocabulary
    {
        public const string PropertyName = LoggingContract.Event;
        public const string SignedIn = "auth.signin.succeeded";
        public const string Booked = "order.line_item.shipped";
        public const string Step2 = "import.step2.finished";

        // Not public, not a constant, not a string: none is part of the vocabulary.
        internal const string Hidden = "Not An Event";
        public static readonly string Field = "Not An Event";
        public const int Number = 3;
    }

    private static class BrokenVocabulary
    {
        public const string TwoParts = "auth.signout";
        public const string SignedIn = "auth.signin.succeeded";
        public const string SignedInAgain = "auth.signin.succeeded";
        public const string Shouting = "Auth.Signin.Refused";
    }

    private class BaseVocabulary
    {
        public const string Inherited = "base.thing";
    }

    private sealed class DerivedVocabulary : BaseVocabulary
    {
        public const string Own = "derived.thing.happened";
    }

    [Theory]
    [InlineData("auth.signin.succeeded")]
    [InlineData("order.line_item.shipped")]
    [InlineData("a.b.c")]
    [InlineData("app.step2.done")]
    public void A_three_part_lowercase_name_is_well_formed(string name)
    {
        Assert.True(EventNames.IsWellFormed(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auth.signout")]
    [InlineData("auth.signin.succeeded.twice")]
    [InlineData("Auth.signin.succeeded")]
    [InlineData("auth.signIn.succeeded")]
    [InlineData("auth..succeeded")]
    [InlineData("auth.signin.")]
    [InlineData(".signin.succeeded")]
    [InlineData("auth.2fa.passed")]
    [InlineData("auth._signin.succeeded")]
    [InlineData("auth.sign-in.succeeded")]
    [InlineData("auth.sign in.succeeded")]
    [InlineData("auth.signin.succeeded\n")]
    [InlineData("auth.signin.succeedéd")]
    public void Anything_else_is_not(string? name)
    {
        Assert.False(EventNames.IsWellFormed(name));
    }

    [Fact]
    public void A_vocabulary_that_holds_has_no_violations()
    {
        Assert.Empty(EventNames.Violations(typeof(GoodVocabulary), nameof(GoodVocabulary.PropertyName)));
    }

    [Fact]
    public void The_property_name_constant_is_reported_unless_it_is_excluded()
    {
        var violation = Assert.Single(EventNames.Violations(typeof(GoodVocabulary)));

        Assert.Equal(
            new EventNameViolation(nameof(GoodVocabulary.PropertyName), "Event", EventNameViolationKind.Malformed),
            violation);
    }

    [Fact]
    public void Malformed_and_duplicate_names_come_back_as_data()
    {
        var violations = EventNames.Violations(typeof(BrokenVocabulary));

        Assert.Equal(
            [
                new EventNameViolation("Shouting", "Auth.Signin.Refused", EventNameViolationKind.Malformed),
                new EventNameViolation("SignedIn", "auth.signin.succeeded", EventNameViolationKind.Duplicate),
                new EventNameViolation("SignedInAgain", "auth.signin.succeeded", EventNameViolationKind.Duplicate),
                new EventNameViolation("TwoParts", "auth.signout", EventNameViolationKind.Malformed)
            ],
            violations);
    }

    [Fact]
    public void A_violation_reads_as_a_sentence_for_a_failure_message()
    {
        var violations = EventNames.Violations(typeof(BrokenVocabulary));

        Assert.Equal(
            "Shouting is 'Auth.Signin.Refused'. Event names are lowercase area.thing.outcome, dots between parts and underscores inside one.",
            violations[0].ToString());
        Assert.Equal(
            "SignedIn is 'auth.signin.succeeded', which another constant also uses. Two constants cannot mean the same event.",
            violations[1].ToString());
    }

    [Fact]
    public void Inherited_constants_are_checked_too()
    {
        var violation = Assert.Single(EventNames.Violations(typeof(DerivedVocabulary)));

        Assert.Equal(nameof(BaseVocabulary.Inherited), violation.Constant);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => EventNames.Violations(null!));
        Assert.Throws<ArgumentNullException>(() => EventNames.Violations(typeof(GoodVocabulary), null!));
    }
}
