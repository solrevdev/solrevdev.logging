using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Pins the startup guard on the three pipeline extensions.
/// </summary>
/// <remarks>
/// <para>
/// Wiring the middleware without calling <c>AddSolrevdevRequestLogging</c> used to start the
/// application and run it on package defaults it never chose. Measured against a running
/// application on the published 1.0.0: it served requests, and
/// <c>IOptions&lt;RequestLogOptions&gt;</c> resolved to a default-constructed instance carrying
/// <c>/healthcheck,/version</c> and a 512 character cap. Every host calls <c>AddOptions</c>, so
/// the options resolving proves nothing about whether this package was registered, and nothing
/// in the pipeline threw.
/// </para>
/// <para>
/// The marker service is the only thing whose presence answers that question, so the guard
/// reads it and both directions are pinned here: absent throws with the framework's wording,
/// present is silent.
/// </para>
/// <para>
/// The scope is one mistake. This does NOT catch a unit test on a bare
/// <c>DefaultHttpContext</c> silently measuring package defaults, which is
/// <see cref="OptionsResolutionTests"/> and a documentation matter. No startup check can see
/// that, because no application started.
/// </para>
/// </remarks>
public class RegistrationGuardTests
{
    private const string ExpectedMessage =
        "Unable to find the required services. Please add all the required services by calling "
        + "'IServiceCollection.AddSolrevdevRequestLogging' in the application startup code.";

    private static IApplicationBuilder Unregistered() =>
        new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());

    private static IApplicationBuilder Registered()
    {
        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging();

        return new ApplicationBuilder(services.BuildServiceProvider());
    }

    public static TheoryData<string, Action<IApplicationBuilder>> PipelineExtensions => new()
    {
        { "UseSolrevdevClientLogContext", app => app.UseSolrevdevClientLogContext() },
        { "UseSolrevdevUserLogContext", app => app.UseSolrevdevUserLogContext() },
        { "UseSolrevdevRequestLogging", app => app.UseSolrevdevRequestLogging() }
    };

    [Theory]
    [MemberData(nameof(PipelineExtensions))]
    public void The_pipeline_refuses_to_wire_without_the_registration(
        string name,
        Action<IApplicationBuilder> use)
    {
        var error = Assert.Throws<InvalidOperationException>(() => use(Unregistered()));

        Assert.Equal(ExpectedMessage, error.Message);
        Assert.Contains("AddSolrevdevRequestLogging", error.Message, StringComparison.Ordinal);
        Assert.NotNull(name);
    }

    [Theory]
    [MemberData(nameof(PipelineExtensions))]
    public void The_pipeline_wires_when_the_registration_is_there(
        string name,
        Action<IApplicationBuilder> use)
    {
        use(Registered());

        Assert.NotNull(name);
    }

    [Fact]
    public void Options_resolving_is_not_evidence_that_the_package_was_registered()
    {
        // The measured fact the guard exists for. AddOptions is called by every host, so
        // IOptions<RequestLogOptions> hands back a default instance either way, and a check
        // written against the options would pass for an application that never registered
        // anything.
        var services = new ServiceCollection();
        services.AddOptions();

        var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLogOptions>>();

        Assert.NotNull(options.Value);
        Assert.Equal(["/healthcheck", "/version"], options.Value.QuietPathPrefixes);
        Assert.Equal(512, options.Value.MaximumUserAgentLength);

        // And the marker, which is what the guard actually reads, is not there.
        Assert.Null(provider.GetService(typeof(RequestLoggingMarkerService)));
    }

    [Fact]
    public void The_registration_adds_the_marker()
    {
        var provider = new ServiceCollection().AddSolrevdevRequestLogging().BuildServiceProvider();

        Assert.NotNull(provider.GetService(typeof(RequestLoggingMarkerService)));
    }

    [Fact]
    public void The_configuring_overload_adds_the_marker_too()
    {
        var provider = new ServiceCollection()
            .AddSolrevdevRequestLogging(options => options.MaximumUserAgentLength = 64)
            .BuildServiceProvider();

        Assert.NotNull(provider.GetService(typeof(RequestLoggingMarkerService)));
    }
}
