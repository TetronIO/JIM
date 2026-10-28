// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Reflection;
using Microsoft.JSInterop;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Web.Logging;

/// <summary>
/// Wraps the configured sinks and demotes benign Blazor Server client-disconnect noise from Error to Warning.
/// When a browser disconnects or navigates away while a circuit is being disposed, the framework's own
/// components (RemoteNavigationManager, CircuitHost) log Error-level events whose root cause is a
/// <see cref="JSDisconnectedException"/>. When the browser is still connected but no longer answering (a frozen
/// background tab), a component's disposal interop instead times out, and the renderer reports the resulting
/// <see cref="TaskCanceledException"/> the same way; the framework's own <c>Virtualize</c> does this. Both carry no
/// diagnostic value beyond "the client went away", so they are demoted to Warning. Genuine circuit failures (any
/// other exception, including an interop timeout outside disposal) pass through unchanged and still log at Error.
/// </summary>
public sealed class CircuitDisconnectDemotingSink : ILogEventSink, IDisposable
{
    private const string FrameworkComponentsSourcePrefix = "Microsoft.AspNetCore.Components";
    private const string JsInteropNamespace = "Microsoft.JSInterop";
    private const string RendererTypeName = "Microsoft.AspNetCore.Components.RenderTree.Renderer";
    private const string RendererDisposalMarker = "ProcessDisposalQueue";
    private const string RuntimeNamespacePrefix = "System.";

    private readonly Logger _innerLogger;

    public CircuitDisconnectDemotingSink(Logger innerLogger)
    {
        _innerLogger = innerLogger;
    }

    /// <summary>
    /// Determines whether a log event is benign client-disconnect noise: an Error (or Fatal) event raised by
    /// the framework's Blazor components whose exception chain contains a <see cref="JSDisconnectedException"/>,
    /// or an interop call that timed out while the renderer was disposing a component.
    /// </summary>
    public static bool IsBenignCircuitDisconnectNoise(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error)
            return false;

        if (!ContainsClientGoneException(logEvent.Exception))
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

    private static bool ContainsClientGoneException(Exception? exception)
    {
        while (exception != null)
        {
            switch (exception)
            {
                case JSDisconnectedException:
                    return true;
                case TaskCanceledException when IsInteropTimeoutDuringDisposal(exception):
                    return true;
                case AggregateException aggregate:
                    return aggregate.InnerExceptions.Any(ContainsClientGoneException);
                default:
                    exception = exception.InnerException;
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a cancellation was raised by a JavaScript interop call (its innermost frame is in
    /// <c>Microsoft.JSInterop</c>, where the call's timeout cancels it) made while the renderer was disposing a
    /// component. Outside disposal a call that never completes can be a JavaScript function that never resolves,
    /// which is a genuine fault, so it is not treated as benign. The runtime's own frames (the awaiter plumbing
    /// between each async frame, which the logged stack trace hides) are skipped.
    /// </summary>
    private static bool IsInteropTimeoutDuringDisposal(Exception exception)
    {
        var frames = new StackTrace(exception, false).GetFrames()
            .Where(frame => frame.GetMethod() is { } method && !IsRuntimePlumbing(method))
            .ToList();
        if (frames.Count == 0 || frames[0].GetMethod()?.DeclaringType?.Namespace != JsInteropNamespace)
            return false;

        return frames.Any(frame =>
            frame.GetMethod() is { DeclaringType.FullName: { } typeName } method &&
            typeName.StartsWith(RendererTypeName, StringComparison.Ordinal) &&
            (typeName.Contains(RendererDisposalMarker, StringComparison.Ordinal) ||
             method.Name.Contains(RendererDisposalMarker, StringComparison.Ordinal)));
    }

    private static bool IsRuntimePlumbing(MethodBase method)
    {
        return method.DeclaringType?.Namespace?.StartsWith(RuntimeNamespacePrefix, StringComparison.Ordinal) == true;
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
