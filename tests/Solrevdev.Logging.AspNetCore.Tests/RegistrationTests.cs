using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Solrevdev.Logging.AspNetCore.Tests;

public class RegistrationTests
{
    [Fact]
    public void The_defaults_are_the_documented_ones()
    {
        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging();

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RequestLogOptions>>().Value;

        Assert.Equal(System.Security.Claims.ClaimTypes.NameIdentifier, options.UserIdClaimType);
        Assert.Equal(System.Security.Claims.ClaimTypes.Email, options.UserEmailClaimType);
        Assert.True(options.FallBackToIdentityNameForEmail);
        Assert.Equal(512, options.MaximumUserAgentLength);
        Assert.Equal(512, options.MaximumRefererLength);
        Assert.Equal(256, options.MaximumForwardedForLength);
        Assert.Equal(["/healthcheck", "/version"], options.QuietPathPrefixes);
    }

    [Fact]
    public void Configuration_reaches_the_options()
    {
        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging(options => options.MaximumUserAgentLength = 64);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<RequestLogOptions>>().Value;

        Assert.Equal(64, options.MaximumUserAgentLength);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddSolrevdevRequestLogging(null!));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddSolrevdevRequestLogging(null!));
        Assert.Throws<ArgumentNullException>(() => ApplicationBuilderExtensions.UseSolrevdevClientLogContext(null!));
        Assert.Throws<ArgumentNullException>(() => ApplicationBuilderExtensions.UseSolrevdevUserLogContext(null!));
        Assert.Throws<ArgumentNullException>(() => SerilogRequestLoggingExtensions.UseSolrevdevRequestLogging(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_header_cap_that_keeps_nothing_is_refused_at_registration(int maximumLength)
    {
        // The caps are the application's own configuration, never a caller's, so startup is
        // the useful place to refuse one. Before this, a negative cap surfaced as an
        // ArgumentOutOfRangeException thrown out of a log enricher on whichever request first
        // carried a user agent, and a zero cap surfaced as a property that silently never
        // appeared, which reads exactly like the enricher being broken.
        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging(options => options.MaximumUserAgentLength = maximumLength);

        var error = Assert.Throws<OptionsValidationException>(
            () => services.BuildServiceProvider().GetRequiredService<IOptions<RequestLogOptions>>().Value);

        Assert.Contains(nameof(RequestLogOptions.MaximumUserAgentLength), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_cap_is_named_by_the_failure_it_causes()
    {
        var services = new ServiceCollection();
        services.AddSolrevdevRequestLogging(options =>
        {
            options.MaximumRefererLength = 0;
            options.MaximumForwardedForLength = -5;
        });

        var error = Assert.Throws<OptionsValidationException>(
            () => services.BuildServiceProvider().GetRequiredService<IOptions<RequestLogOptions>>().Value);

        Assert.Contains(nameof(RequestLogOptions.MaximumRefererLength), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RequestLogOptions.MaximumForwardedForLength), error.Message, StringComparison.Ordinal);
    }
}
