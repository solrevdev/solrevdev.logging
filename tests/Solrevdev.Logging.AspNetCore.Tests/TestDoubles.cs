using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Solrevdev.Logging.AspNetCore.Tests;

/// <summary>
/// Both classes swap the process-global <see cref="Serilog.Log.Logger"/> to capture events, so
/// they share one xUnit collection and never run at the same time. Collections are the unit of
/// parallelism: without this, one class restores the previous logger while the other is still
/// writing to its sink, and the loser sees an empty sink rather than a wrong value.
/// </summary>
[CollectionDefinition(SerilogLoggerCollection.Name)]
public sealed class SerilogLoggerCollection
{
    public const string Name = "Serilog global logger";
}

/// <summary>
/// Records what an enricher set, so a test can assert the property SET rather than a rendered
/// message. The contract test depends on seeing exactly the keys and no others.
/// </summary>
internal sealed class RecordingDiagnosticContext : IDiagnosticContext
{
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);

    public void Set(string propertyName, object? value, bool destructureObjects = false) =>
        Properties[propertyName] = value;

    public void SetException(Exception exception)
    {
        // Not used by these tests. The interface gained it in Serilog.Extensions.Hosting 8.
    }

    public string? String(string propertyName) =>
        Properties.TryGetValue(propertyName, out var value) ? value as string : null;
}

/// <summary>
/// Collects log events so a test can assert what the LogContext scopes actually put on them.
/// </summary>
internal sealed class CollectingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = [];

    public void Emit(LogEvent logEvent) => Events.Add(logEvent);
}
