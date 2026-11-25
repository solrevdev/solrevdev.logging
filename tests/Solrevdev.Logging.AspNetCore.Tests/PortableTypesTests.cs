using System.Reflection;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Holds the types that may one day move to a package with no ASP.NET dependency.
/// </summary>
/// <remarks>
/// MAINTAINING.md lists them under "Considered, not done". The split was asked for and not
/// made, and it stays cheap only while none of these names an ASP.NET, dependency injection
/// or Serilog type. This reads signatures: fields, properties, parameters, return types, base
/// types and interfaces. It cannot see inside a method body, so a review still has to.
/// </remarks>
public class PortableTypesTests
{
    private static readonly string[] Forbidden =
    [
        "Microsoft.AspNetCore.", "Microsoft.Extensions.", "Microsoft.Net.Http.", "Serilog"
    ];

    public static TheoryData<Type> PortableTypes =>
    [
        typeof(SelfLogOutput),
        typeof(TransportExceptionLog),
        typeof(HeaderValue),
        typeof(EventNames),
        typeof(EventNameViolation),
        typeof(EventNameViolationKind),
        typeof(ILoggedRefusal),
        typeof(LoggedRefusal),
        typeof(BootstrapLogIdentity),
        typeof(BootstrapHost),
        typeof(RefusalEvents),
        typeof(LoggingContract)
    ];

    [Theory]
    [MemberData(nameof(PortableTypes))]
    public void A_portable_type_names_no_aspnet_or_serilog_type_in_its_signatures(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var named = new List<Type?> { type.BaseType };
        named.AddRange(type.GetInterfaces());
        named.AddRange(type.GetFields(all).Select(field => field.FieldType));
        named.AddRange(type.GetProperties(all).Select(property => property.PropertyType));
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            named.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
            if (method is MethodInfo withReturn)
            {
                named.Add(withReturn.ReturnType);
            }
        }

        var offenders = named
            .SelectMany(Flatten)
            .Select(found => found.FullName ?? found.Name)
            .Where(name => Forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0, $"{type.Name} names {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void The_check_can_fail()
    {
        // Without this the theory would pass on a list of types it could not read.
        Assert.Contains(
            typeof(RequestLogOptions).GetProperties().SelectMany(property => Flatten(property.PropertyType)),
            found => (found.FullName ?? string.Empty).StartsWith("Serilog", StringComparison.Ordinal));
    }

    private static IEnumerable<Type> Flatten(Type? type)
    {
        if (type is null)
        {
            yield break;
        }

        yield return type;

        if (type.HasElementType)
        {
            foreach (var inner in Flatten(type.GetElementType()))
            {
                yield return inner;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var inner in Flatten(argument))
            {
                yield return inner;
            }
        }
    }
}
