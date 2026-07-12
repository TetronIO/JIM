// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.JSInterop;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Web.Logging;

/// <summary>
/// Wraps the configured sinks and demotes benign Blazor Server client-disconnect noise from Error to Warning.
/// When a browser disconnects or navigates away while a circuit is being disposed, the framework's own
/// components (RemoteNavigationManager, CircuitHost) log Error-level events whose root cause is a
/// <see cref="JSDisconnectedException"/>. These carry no diagnostic value beyond "the client went away",
/// so they are demoted to Warning. Genuine circuit failures (any other exception type) pass through
/// unchanged and still log at Error.
/// </summary>
public sealed class CircuitDisconnectDemotingSink : ILogEventSink, IDisposable
{
    private const string FrameworkComponentsSourcePrefix = "Microsoft.AspNetCore.Components";

    private readonly Logger _innerLogger;

    public CircuitDisconnectDemotingSink(Logger innerLogger)
    {
        _innerLogger = innerLogger;
    }

    /// <summary>
    /// Determines whether a log event is benign client-disconnect noise: an Error (or Fatal) event raised by
    /// the framework's Blazor components whose exception chain contains a <see cref="JSDisconnectedException"/>.
    /// </summary>
    public static bool IsBenignCircuitDisconnectNoise(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error)
            return false;

        if (!ContainsJsDisconnectedException(logEvent.Exception))
            return false;

        return logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var sourceContext) &&
               sourceContext is ScalarValue { Value: string source } &&
               source.StartsWith(FrameworkComponentsSourcePrefix, StringComparison.Ordinal);
    }

    public void Emit(LogEvent logEvent)
    {
        _innerLogger.Write(IsBenignCircuitDisconnectNoise(logEvent) ? DemoteToWarning(logEvent) : logEvent);
    }

    public void Dispose()
    {
        _innerLogger.Dispose();
    }

    private static bool ContainsJsDisconnectedException(Exception? exception)
    {
        while (exception != null)
        {
            switch (exception)
            {
                case JSDisconnectedException:
                    return true;
                case AggregateException aggregate:
                    return aggregate.InnerExceptions.Any(ContainsJsDisconnectedException);
                default:
                    exception = exception.InnerException;
                    break;
            }
        }

        return false;
    }

    private static LogEvent DemoteToWarning(LogEvent logEvent)
    {
        return new LogEvent(
            logEvent.Timestamp,
            LogEventLevel.Warning,
            logEvent.Exception,
            logEvent.MessageTemplate,
            logEvent.Properties.Select(property => new LogEventProperty(property.Key, property.Value)),
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default);
    }
}
