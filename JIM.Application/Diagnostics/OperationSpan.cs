using System.Diagnostics;

namespace JIM.Application.Diagnostics;

/// <summary>
/// Wraps System.Diagnostics.Activity to avoid naming conflicts with JIM's Activity (audit/task tracking).
/// Represents a timed operation span for performance diagnostics.
/// </summary>
public sealed class OperationSpan : IDisposable
{
    private readonly Activity? _activity;

    internal OperationSpan(Activity? activity)
    {
        _activity = activity;
    }

    /// <summary>
    /// Gets the unique identifier for this span.
    /// </summary>
    public string? Id => _activity?.Id;

    /// <summary>
    /// Gets the name/operation of this span.
    /// </summary>
    public string? Name => _activity?.DisplayName;

    /// <summary>
    /// Gets the duration of this span. Only valid after the span is stopped/disposed.
    /// </summary>
    public TimeSpan Duration => _activity?.Duration ?? TimeSpan.Zero;

    /// <summary>
    /// Gets the start time of this span in UTC.
    /// </summary>
    public DateTime StartTimeUtc => _activity?.StartTimeUtc ?? DateTime.MinValue;

    /// <summary>
    /// Adds a tag (key-value pair) to this span for additional context.
    /// </summary>
    /// <param name="key">The tag key.</param>
    /// <param name="value">The tag value.</param>
    /// <returns>This span for fluent chaining.</returns>
    public OperationSpan SetTag(string key, object? value)
    {
        _activity?.SetTag(key, value);
        return this;
    }

    /// <summary>
    /// Adds an event to this span, useful for marking significant points during the operation.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <returns>This span for fluent chaining.</returns>
    public OperationSpan AddEvent(string name)
    {
        _activity?.AddEvent(new ActivityEvent(name));
        return this;
    }

    /// <summary>
    /// Adds an event with tags to this span.
    /// </summary>
    /// <param name="name">The event name.</param>
    /// <param name="tags">Tags to attach to the event.</param>
    /// <returns>This span for fluent chaining.</returns>
    public OperationSpan AddEvent(string name, params KeyValuePair<string, object?>[] tags)
    {
        _activity?.AddEvent(new ActivityEvent(name, tags: new ActivityTagsCollection(tags)));
        return this;
    }

    /// <summary>
    /// Marks this span as having an error.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    /// <returns>This span for fluent chaining.</returns>
    public OperationSpan SetError(Exception exception)
    {
        _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        _activity?.SetTag("error.type", exception.GetType().Name);
        _activity?.SetTag("error.message", exception.Message);
        return this;
    }

    /// <summary>
    /// Marks this span as successful.
    /// </summary>
    /// <returns>This span for fluent chaining.</returns>
    public OperationSpan SetSuccess()
    {
        _activity?.SetStatus(ActivityStatusCode.Ok);
        return this;
    }

    /// <summary>
    /// Stops the span and records its duration.
    /// </summary>
    public void Dispose()
    {
        _activity?.Dispose();
    }
}
