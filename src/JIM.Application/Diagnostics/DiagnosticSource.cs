using System.Diagnostics;

namespace JIM.Application.Diagnostics;

/// <summary>
/// Wraps System.Diagnostics.ActivitySource to provide a clean API for creating operation spans.
/// Use this instead of ActivitySource directly to avoid naming conflicts with JIM's Activity class.
/// </summary>
public sealed class DiagnosticSource : IDisposable
{
    private readonly ActivitySource _source;

    /// <summary>
    /// Creates a new diagnostic source with the specified name.
    /// </summary>
    /// <param name="name">The name of the source, typically the component name (e.g., "JIM.Worker.Sync").</param>
    public DiagnosticSource(string name)
    {
        _source = new ActivitySource(name);
    }

    /// <summary>
    /// Creates a new diagnostic source with the specified name and version.
    /// </summary>
    /// <param name="name">The name of the source.</param>
    /// <param name="version">The version of the source.</param>
    public DiagnosticSource(string name, string version)
    {
        _source = new ActivitySource(name, version);
    }

    /// <summary>
    /// Gets the name of this diagnostic source.
    /// </summary>
    public string Name => _source.Name;

    /// <summary>
    /// Starts a new operation span with the specified name.
    /// The span is automatically linked to any parent span in the current context.
    /// </summary>
    /// <param name="name">The name of the operation being traced.</param>
    /// <returns>An OperationSpan that should be disposed when the operation completes.</returns>
    public OperationSpan StartSpan(string name)
    {
        return new OperationSpan(_source.StartActivity(name));
    }

    /// <summary>
    /// Starts a new operation span with the specified name and kind.
    /// </summary>
    /// <param name="name">The name of the operation being traced.</param>
    /// <param name="kind">The kind of span (internal, client, server, producer, consumer).</param>
    /// <returns>An OperationSpan that should be disposed when the operation completes.</returns>
    public OperationSpan StartSpan(string name, SpanKind kind)
    {
        return new OperationSpan(_source.StartActivity(name, ToActivityKind(kind)));
    }

    /// <summary>
    /// Disposes the underlying ActivitySource.
    /// </summary>
    public void Dispose()
    {
        _source.Dispose();
    }

    private static ActivityKind ToActivityKind(SpanKind kind)
    {
        return kind switch
        {
            SpanKind.Internal => ActivityKind.Internal,
            SpanKind.Client => ActivityKind.Client,
            SpanKind.Server => ActivityKind.Server,
            SpanKind.Producer => ActivityKind.Producer,
            SpanKind.Consumer => ActivityKind.Consumer,
            _ => ActivityKind.Internal
        };
    }
}

/// <summary>
/// Describes the relationship between the span and its parent/children.
/// </summary>
public enum SpanKind
{
    /// <summary>
    /// Default. Represents an internal operation within an application.
    /// </summary>
    Internal,

    /// <summary>
    /// Represents a request to an external service.
    /// </summary>
    Client,

    /// <summary>
    /// Represents handling a request from an external source.
    /// </summary>
    Server,

    /// <summary>
    /// Represents producing a message to a queue/topic.
    /// </summary>
    Producer,

    /// <summary>
    /// Represents consuming a message from a queue/topic.
    /// </summary>
    Consumer
}
