namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Ported with the code, with the default application name passed in.
/// </summary>
public class BootstrapLogIdentityTests
{
    private const string DefaultApplication = "example-app";

    [Fact]
    public void Bootstrap_identity_defaults_without_reading_configuration_files()
    {
        var identity = BootstrapLogIdentity.Resolve([], DefaultApplication, _ => null);

        Assert.Equal(DefaultApplication, identity.Application);
        Assert.Equal("Production", identity.EnvironmentName);
        Assert.Equal(BootstrapLogIdentity.DefaultEnvironmentName, identity.EnvironmentName);
    }

    [Fact]
    public void Bootstrap_identity_uses_the_application_variable_and_host_environment_precedence()
    {
        var values = new Dictionary<string, string>
        {
            ["Serilog__Properties__Application"] = "example-app.review",
            ["DOTNET_ENVIRONMENT"] = "Staging",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
        };

        var identity = BootstrapLogIdentity.Resolve([], DefaultApplication, key => values.GetValueOrDefault(key));

        Assert.Equal("example-app.review", identity.Application);
        Assert.Equal("Staging", identity.EnvironmentName);
    }

    [Theory]
    [InlineData("--environment=Testing")]
    [InlineData("/environment=Testing")]
    [InlineData("environment=Testing")]
    public void Bootstrap_command_line_overrides_environment_variables(string argument)
    {
        var identity = BootstrapLogIdentity.Resolve(
            [argument, "--Serilog:Properties:Application", "example-app.smoke"],
            DefaultApplication,
            _ => "Development");

        Assert.Equal("Testing", identity.EnvironmentName);
        Assert.Equal("example-app.smoke", identity.Application);
    }

    [Fact]
    public void Bootstrap_uses_last_spaced_environment_option_and_ignores_unrelated_values()
    {
        var identity = BootstrapLogIdentity.Resolve(
            ["--environment", "Staging", "--environment", "Testing", "--urls=http://127.0.0.1:5999"],
            DefaultApplication,
            _ => null);

        Assert.Equal("Testing", identity.EnvironmentName);
    }

    [Fact]
    public void Bootstrap_falls_back_to_aspnetcore_environment()
    {
        var identity = BootstrapLogIdentity.Resolve(
            [],
            DefaultApplication,
            key => key == "ASPNETCORE_ENVIRONMENT" ? "Development" : null);

        Assert.Equal("Development", identity.EnvironmentName);
    }

    [Fact]
    public void A_blank_variable_counts_as_not_given()
    {
        var identity = BootstrapLogIdentity.Resolve([], DefaultApplication, _ => "  ");

        Assert.Equal(DefaultApplication, identity.Application);
        Assert.Equal("Production", identity.EnvironmentName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_default_application_is_refused(string? defaultApplication)
    {
        // Absent, never blank: a bootstrap event with an empty Application is the row nobody
        // can filter on.
        Assert.ThrowsAny<ArgumentException>(() => BootstrapLogIdentity.Resolve([], defaultApplication!, _ => null));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => BootstrapLogIdentity.Resolve(null!, DefaultApplication));
    }
}
