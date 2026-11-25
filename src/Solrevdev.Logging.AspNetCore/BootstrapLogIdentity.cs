namespace Solrevdev.Logging.AspNetCore;

/// <summary>
/// Names startup events before configuration files or the host can be loaded.
/// </summary>
/// <remarks>
/// <para>
/// A bootstrap logger exists to report that configuration
/// failed to load, so it cannot read <see cref="LoggingContract.Application"/> and
/// <see cref="LoggingContract.Environment"/> from configuration. Without them its events land
/// in the shared log store under no application at all.
/// </para>
/// <para>
/// So both come from launch arguments and environment variables only, in the order the host
/// itself would apply them, and no file is read.
/// </para>
/// <para>
/// <strong>That order depends on which host the application builds, and it was measured, not
/// read.</strong> With <c>DOTNET_ENVIRONMENT</c> and <c>ASPNETCORE_ENVIRONMENT</c> both set to
/// different values, on .NET 10.0.15 and .NET 11.0.0:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>WebApplication.CreateBuilder</c> and <c>CreateSlimBuilder</c> take
/// <c>DOTNET_ENVIRONMENT</c>.
/// </description></item>
/// <item><description>
/// <c>Host.CreateDefaultBuilder().ConfigureWebHostDefaults</c> takes
/// <c>ASPNETCORE_ENVIRONMENT</c>, and takes it over a <c>--environment</c> launch argument
/// as well.
/// </description></item>
/// <item><description>
/// <c>Host.CreateApplicationBuilder</c>, a worker, takes <c>DOTNET_ENVIRONMENT</c> and never
/// reads <c>ASPNETCORE_ENVIRONMENT</c> at all.
/// </description></item>
/// </list>
/// <para>
/// The first row is the opposite of what is commonly written about the web host, which is why
/// <see cref="BootstrapHost"/> exists and why a test builds each host and compares. Pass the
/// kind your <c>Program.cs</c> builds, or the first events of a process land under one
/// <see cref="LoggingContract.Environment"/> and the rest under another.
/// </para>
/// </remarks>
/// <param name="Application">The value for <see cref="LoggingContract.Application"/>.</param>
/// <param name="EnvironmentName">The value for <see cref="LoggingContract.Environment"/>.</param>
public sealed record BootstrapLogIdentity(string Application, string EnvironmentName)
{
    /// <summary>
    /// The environment name used when nothing sets one, which is the host's own default.
    /// </summary>
    public const string DefaultEnvironmentName = "Production";

    private const string ApplicationOption = "Serilog:Properties:" + LoggingContract.Application;
    private const string ApplicationVariable = "Serilog__Properties__" + LoggingContract.Application;
    private const string EnvironmentOption = "environment";
    private const string DotnetEnvironmentVariable = "DOTNET_ENVIRONMENT";
    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    /// <summary>
    /// Works out the application and environment names from launch arguments and environment
    /// variables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application name is the <c>Serilog:Properties:Application</c> launch argument,
    /// then the <c>Serilog__Properties__Application</c> environment variable, then
    /// <paramref name="defaultApplication"/>.
    /// </para>
    /// <para>
    /// The environment name comes from the <c>environment</c> launch argument and the two
    /// environment variables, in the order <paramref name="host"/> gives, then
    /// <see cref="DefaultEnvironmentName"/>. For the default,
    /// <see cref="BootstrapHost.WebApplication"/>, that is the argument, then
    /// <c>DOTNET_ENVIRONMENT</c>, then <c>ASPNETCORE_ENVIRONMENT</c>. See
    /// <see cref="BootstrapHost"/> for the other two.
    /// </para>
    /// <para>
    /// An argument may be written <c>--name=value</c>, <c>/name=value</c>, <c>name=value</c>
    /// or <c>--name value</c>. When one is repeated the last wins, as it does for the host. A
    /// blank value counts as not given.
    /// </para>
    /// </remarks>
    /// <param name="args">The process's launch arguments.</param>
    /// <param name="defaultApplication">
    /// The application name to use when neither an argument nor a variable gives one. Use the
    /// value the application's own configuration file sets, so bootstrap events and later ones
    /// carry the same name.
    /// </param>
    /// <param name="readEnvironment">
    /// Reads an environment variable. Null means
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>; a test passes its own.
    /// </param>
    /// <param name="host">
    /// Which host the application goes on to build, which decides the order of the two
    /// environment variables. Defaults to <see cref="BootstrapHost.WebApplication"/>.
    /// </param>
    /// <returns>The two names.</returns>
    public static BootstrapLogIdentity Resolve(
        string[] args,
        string defaultApplication,
        Func<string, string?>? readEnvironment = null,
        BootstrapHost host = BootstrapHost.WebApplication)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultApplication);

        readEnvironment ??= Environment.GetEnvironmentVariable;

        return new BootstrapLogIdentity(
            Argument(args, ApplicationOption)
                ?? Present(readEnvironment(ApplicationVariable))
                ?? defaultApplication,
            SettledEnvironment(args, readEnvironment, host) ?? DefaultEnvironmentName);
    }

    private static string? SettledEnvironment(string[] args, Func<string, string?> readEnvironment, BootstrapHost host) =>
        host switch
        {
            // Measured, and not the order anyone would guess: ConfigureWebHostDefaults adds
            // the ASPNETCORE_ variables after the launch arguments, so the variable beats
            // --environment. The argument still beats DOTNET_ENVIRONMENT.
            BootstrapHost.WebHostDefaults =>
                Present(readEnvironment(AspNetCoreEnvironmentVariable))
                ?? Argument(args, EnvironmentOption)
                ?? Present(readEnvironment(DotnetEnvironmentVariable)),

            // A worker's host has no ASPNETCORE_ prefix to read.
            BootstrapHost.Worker =>
                Argument(args, EnvironmentOption)
                ?? Present(readEnvironment(DotnetEnvironmentVariable)),

            _ =>
                Argument(args, EnvironmentOption)
                ?? Present(readEnvironment(DotnetEnvironmentVariable))
                ?? Present(readEnvironment(AspNetCoreEnvironmentVariable))
        };

    private static string? Argument(string[] args, string name)
    {
        string? result = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            var option = argument.StartsWith("--", StringComparison.Ordinal) ? argument[2..]
                : argument.StartsWith('/') ? argument[1..] : argument;
            var separator = option.IndexOf('=');
            if (separator >= 0 && option[..separator].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                result = Present(option[(separator + 1)..]);
            }
            else if (option.Equals(name, StringComparison.OrdinalIgnoreCase)
                && argument != option && index + 1 < args.Length
                && !args[index + 1].StartsWith('-') && !args[index + 1].StartsWith('/'))
            {
                result = Present(args[++index]);
            }
        }

        return result;
    }

    private static string? Present(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Which host an application builds, for <see cref="BootstrapLogIdentity.Resolve"/>. The
/// hosts read the two environment variables in different orders.
/// </summary>
public enum BootstrapHost
{
    /// <summary>
    /// <c>WebApplication.CreateBuilder</c> or <c>CreateSlimBuilder</c>: the
    /// <c>--environment</c> argument, then <c>DOTNET_ENVIRONMENT</c>, then
    /// <c>ASPNETCORE_ENVIRONMENT</c>.
    /// </summary>
    WebApplication = 0,

    /// <summary>
    /// <c>Host.CreateDefaultBuilder().ConfigureWebHostDefaults</c>, the <c>Startup</c> class
    /// shape: <c>ASPNETCORE_ENVIRONMENT</c>, then the <c>--environment</c> argument, then
    /// <c>DOTNET_ENVIRONMENT</c>.
    /// </summary>
    /// <remarks>
    /// The variable beats the argument here, measured on .NET 10 and .NET 11: this host adds
    /// the <c>ASPNETCORE_</c> variables after the launch arguments.
    /// Not the older <c>WebHost.CreateDefaultBuilder</c>, which reads
    /// <c>ASPNETCORE_ENVIRONMENT</c> alone and is not modelled here.
    /// </remarks>
    WebHostDefaults = 1,

    /// <summary>
    /// <c>Host.CreateApplicationBuilder</c> or <c>Host.CreateDefaultBuilder</c> with no web
    /// host: the <c>--environment</c> argument, then <c>DOTNET_ENVIRONMENT</c>.
    /// <c>ASPNETCORE_ENVIRONMENT</c> is never read.
    /// </summary>
    Worker = 2
}
