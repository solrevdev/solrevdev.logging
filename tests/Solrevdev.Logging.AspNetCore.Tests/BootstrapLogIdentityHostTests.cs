using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// These tests set real process environment variables, which every test in the process can
/// see, so nothing else may run beside them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>
/// Holds <see cref="BootstrapLogIdentity"/> to the host it stands in for, by building the
/// host and comparing.
/// </summary>
/// <remarks>
/// <para>
/// The bootstrap logger names startup events before the host exists. If it works the
/// environment out differently from the host, the first events of a process land in the log
/// store under one <c>Environment</c> and the rest under another.
/// </para>
/// <para>
/// It was reported that <c>ASPNETCORE_ENVIRONMENT</c> overrides <c>DOTNET_ENVIRONMENT</c> for
/// a web host, which would have made the default order wrong. Measured here on .NET 10 and
/// .NET 11, that is true of <c>Host.CreateDefaultBuilder().ConfigureWebHostDefaults</c> and
/// FALSE of <c>WebApplication.CreateBuilder</c>, where <c>DOTNET_ENVIRONMENT</c> wins. A
/// worker's host never reads <c>ASPNETCORE_ENVIRONMENT</c> at all. Three hosts, three
/// answers, so <see cref="BootstrapHost"/> names which one the application builds.
/// </para>
/// <para>
/// Every case below asks the real host and asserts the two agree, so a future runtime that
/// changes an order fails here.
/// </para>
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public class BootstrapLogIdentityHostTests
{
    private const string Dotnet = "DOTNET_ENVIRONMENT";
    private const string AspNetCore = "ASPNETCORE_ENVIRONMENT";

    public static TheoryData<string?, string?, string[]> Cases => new()
    {
        // Neither variable: the host's own default.
        { null, null, [] },
        { "Staging", null, [] },
        { null, "Development", [] },
        // Both set and different. This is the row the old order got wrong.
        { "Staging", "Development", [] },
        { "Development", "Staging", [] },
        // A launch argument, in every spelling the host reads. It beats both variables for a
        // WebApplication and a worker. ConfigureWebHostDefaults lets ASPNETCORE_ENVIRONMENT
        // beat it, which is pinned on its own below.
        { "Staging", "Development", ["--environment=Testing"] },
        { "Staging", "Development", ["/environment=Testing"] },
        { "Staging", "Development", ["environment=Testing"] },
        { "Staging", "Development", ["--environment", "Testing"] },
        { "Staging", "Development", ["/environment", "Testing"] },
        { "Staging", "Development", ["--ENVIRONMENT=Testing"] },
        // Repeated: the last one wins.
        { "Staging", "Development", ["--environment", "Review", "--environment=Testing"] },
        // An unrelated argument changes nothing.
        { "Staging", "Development", ["--urls=http://127.0.0.1:5999"] },
        { null, "Development", ["--environment=Testing"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_bootstrap_environment_is_the_one_each_host_settles_on(
        string? dotnetEnvironment,
        string? aspNetCoreEnvironment,
        string[] args)
    {
        foreach (var host in Enum.GetValues<BootstrapHost>())
        {
            var (hostEnvironment, bootstrap) = Measure(host, dotnetEnvironment, aspNetCoreEnvironment, args);

            Assert.True(
                hostEnvironment == bootstrap.EnvironmentName,
                $"{host}: the host settled on {hostEnvironment} and the bootstrap identity on {bootstrap.EnvironmentName}.");
        }
    }

    [Theory]
    // The runtime facts, asserted on their own so the reason for each order is on record and
    // not only the agreement.
    [InlineData(BootstrapHost.WebApplication, "Staging", "Development", "Staging")]
    [InlineData(BootstrapHost.WebHostDefaults, "Staging", "Development", "Development")]
    [InlineData(BootstrapHost.Worker, "Staging", "Development", "Staging")]
    // The launch argument against both variables.
    [InlineData(BootstrapHost.WebApplication, "Staging", "Development", "Testing", "--environment=Testing")]
    [InlineData(BootstrapHost.WebHostDefaults, "Staging", "Development", "Development", "--environment=Testing")]
    [InlineData(BootstrapHost.WebHostDefaults, "Staging", null, "Testing", "--environment=Testing")]
    [InlineData(BootstrapHost.Worker, "Staging", "Development", "Testing", "--environment=Testing")]
    // Only one of the two set.
    [InlineData(BootstrapHost.WebApplication, null, "Development", "Development")]
    [InlineData(BootstrapHost.WebHostDefaults, "Staging", null, "Staging")]
    [InlineData(BootstrapHost.Worker, null, "Development", "Production")]
    // Neither.
    [InlineData(BootstrapHost.WebApplication, null, null, "Production")]
    [InlineData(BootstrapHost.WebHostDefaults, null, null, "Production")]
    [InlineData(BootstrapHost.Worker, null, null, "Production")]
    public void Each_host_reads_the_two_variables_in_its_own_order(
        BootstrapHost host,
        string? dotnetEnvironment,
        string? aspNetCoreEnvironment,
        string expected,
        params string[] args)
    {
        var (hostEnvironment, bootstrap) = Measure(host, dotnetEnvironment, aspNetCoreEnvironment, args);

        Assert.Equal(expected, hostEnvironment);
        Assert.Equal(expected, bootstrap.EnvironmentName);
    }

    [Fact]
    public void The_default_host_kind_is_the_web_application()
    {
        var values = new Dictionary<string, string> { [Dotnet] = "Staging", [AspNetCore] = "Development" };

        Assert.Equal(
            BootstrapLogIdentity.Resolve([], "example-app", values.GetValueOrDefault, BootstrapHost.WebApplication),
            BootstrapLogIdentity.Resolve([], "example-app", values.GetValueOrDefault));
        Assert.Equal(BootstrapLogIdentity.DefaultEnvironmentName, Environments.Production);
    }

    private static string EnvironmentOf(BootstrapHost host, string[] args) => host switch
    {
        // Never started: building is enough to settle the environment name.
        BootstrapHost.WebApplication =>
            WebApplication.CreateBuilder(args).Build().Environment.EnvironmentName,

        BootstrapHost.WebHostDefaults =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(web => web.Configure(_ => { }))
                .Build().Services.GetRequiredService<IHostEnvironment>().EnvironmentName,

        _ => Host.CreateApplicationBuilder(args)
            .Build().Services.GetRequiredService<IHostEnvironment>().EnvironmentName
    };

    private static (string Host, BootstrapLogIdentity Bootstrap) Measure(
        BootstrapHost host,
        string? dotnetEnvironment,
        string? aspNetCoreEnvironment,
        string[] args)
    {
        var previousDotnet = Environment.GetEnvironmentVariable(Dotnet);
        var previousAspNetCore = Environment.GetEnvironmentVariable(AspNetCore);

        Environment.SetEnvironmentVariable(Dotnet, dotnetEnvironment);
        Environment.SetEnvironmentVariable(AspNetCore, aspNetCoreEnvironment);

        try
        {
            var bootstrap = BootstrapLogIdentity.Resolve(args, "example-app", host: host);

            return (EnvironmentOf(host, args), bootstrap);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Dotnet, previousDotnet);
            Environment.SetEnvironmentVariable(AspNetCore, previousAspNetCore);
        }
    }
}
