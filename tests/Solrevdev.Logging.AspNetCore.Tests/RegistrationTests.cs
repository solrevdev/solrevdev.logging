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
}
