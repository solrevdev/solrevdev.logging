namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Pins the public type names, because a new one is a source break for some consumers.
/// </summary>
/// <remarks>
/// Measured on the 1.3.0 release candidate: an application that held a local copy of a ported
/// type and imported this namespace got CS0104, ambiguous reference, from the version bump
/// alone. The README and the changelog list the new names so a consumer can search for a
/// clash before upgrading. This fails when a public type is added or removed, which is the
/// moment to update both lists.
/// </remarks>
public class PublicSurfaceTests
{
    private static readonly string[] In120 =
    [
        "ApplicationBuilderExtensions", "ClientAddress", "ClientLogContextMiddleware",
        "ForwardedForHeader", "LoggingContract", "RefererHeader", "RequestLogLevel",
        "RequestLogOptions", "RequestSummaryEnricher", "SerilogRequestLoggingExtensions",
        "ServiceCollectionExtensions", "UserLogContextMiddleware"
    ];

    private static readonly string[] AddedIn130 =
    [
        "AntiforgeryRefusalEndpointFilter", "AntiforgeryRefusalLog", "AntiforgeryRefusalLogFilter",
        "BootstrapHost", "BootstrapLogIdentity", "EventNameViolation", "EventNameViolationKind",
        "EventNames", "FormRefusalLogFilter", "HeaderValue", "ILoggedRefusal", "LoggedRefusal",
        "RefusalEvents", "RefusalLogOptions", "RefusalLoggingExtensions", "RequestNavigation",
        "RequestQuery", "SelfLogOutput", "TransportExceptionLog"
    ];

    [Fact]
    public void The_public_types_are_the_twelve_from_1_2_0_and_the_nineteen_added_in_1_3_0()
    {
        var exported = typeof(LoggingContract).Assembly.GetExportedTypes().Select(type => type.Name);

        Assert.Equal(In120.Concat(AddedIn130).Order(StringComparer.Ordinal), exported.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_public_type_is_in_the_one_namespace()
    {
        // One using directive brings all of them in, which is why a local type of the same
        // name clashes.
        Assert.All(
            typeof(LoggingContract).Assembly.GetExportedTypes(),
            type => Assert.Equal("Solrevdev.Logging.AspNetCore", type.Namespace));
    }
}
