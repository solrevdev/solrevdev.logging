using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Solrevdev.Logging.AspNetCore.Tests;

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
