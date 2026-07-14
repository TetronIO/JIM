// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Logging;
using Microsoft.JSInterop;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class CircuitDisconnectDemotingSinkTests
{
    private const string RemoteNavigationManagerSource = "Microsoft.AspNetCore.Components.Server.Circuits.RemoteNavigationManager";
    private const string CircuitHostSource = "Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost";

    #region IsBenignCircuitDisconnectNoise tests

    [Test]
    public void IsBenignCircuitDisconnectNoise_NavigationFailedWithJsDisconnectedException_ReturnsTrue()
    {
        var logEvent = CreateLogEvent(
            LogEventLevel.Error,
            new JSDisconnectedException("JavaScript interop calls cannot be issued at this time."),
            RemoteNavigationManagerSource,
            "Navigation failed when changing the location to {Uri}");

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.True);
    }

    [Test]
    public void IsBenignCircuitDisconnectNoise_CircuitErrorWithJsDisconnectedException_ReturnsTrue()
    {
        var logEvent = CreateLogEvent(
            LogEventLevel.Error,
            new JSDisconnectedException("JavaScript interop calls cannot be issued at this time."),
            CircuitHostSource,
            "Unhandled exception in circuit '{CircuitId}'.");

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.True);
    }

    [Test]
    public void IsBenignCircuitDisconnectNoise_JsDisconnectedExceptionAsInnerException_ReturnsTrue()
    {
        var exception = new InvalidOperationException(
            "Render failed.",
            new JSDisconnectedException("JavaScript interop calls cannot be issued at this time."));
        var logEvent = CreateLogEvent(LogEventLevel.Error, exception, CircuitHostSource);

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.True);
    }

    [Test]
    public void IsBenignCircuitDisconnectNoise_JsDisconnectedExceptionWithinAggregateException_ReturnsTrue()
    {
        var exception = new AggregateException(
            new InvalidOperationException("Unrelated."),
            new JSDisconnectedException("JavaScript interop calls cannot be issued at this time."));
        var logEvent = CreateLogEvent(LogEventLevel.Error, exception, CircuitHostSource);

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.True);
    }

    [Test]
    public void IsBenignCircuitDisconnectNoise_CircuitErrorWithOtherException_ReturnsFalse()
    {
        var logEvent = CreateLogEvent(
            LogEventLevel.Error,
            new NullReferenceException("Object reference not set to an instance of an object."),
            CircuitHostSource,
            "Unhandled exception in circuit '{CircuitId}'.");

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.False);
    }

    [Test]
    public void IsBenignCircuitDisconnectNoise_JsDisconnectedExceptionFromApplicationSource_ReturnsFalse()
    {
        var logEvent = CreateLogEvent(
            LogEventLevel.Error,
            new JSDisconnectedException("JavaScript interop calls cannot be issued at this time."),
            "JIM.Web.Services.UserPreferenceService");

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.False);
    }

    [Test]
    public void IsBenignCircuitDisconnectNoise_WarningLevelEvent_ReturnsFalse()
    {
        var logEvent = CreateLogEvent(
            LogEventLevel.Warning,
            new JSDisconnectedException("JavaScript interop calls cannot be issued at this time."),
            CircuitHostSource);

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.False);
    }

    [Test]
    public void IsBenignCircuitDisconnectNoise_ErrorWithoutException_ReturnsFalse()
    {
        var logEvent = CreateLogEvent(LogEventLevel.Error, null, RemoteNavigationManagerSource);

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.False);
    }

    [Test]
    public void IsBenignCircuitDisconnectNoise_ErrorWithoutSourceContext_ReturnsFalse()
    {
        var logEvent = CreateLogEvent(
            LogEventLevel.Error,
            new JSDisconnectedException("JavaScript interop calls cannot be issued at this time."),
            sourceContext: null);

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.False);
    }

    #endregion

    #region Emit tests

    [Test]
    public void Emit_BenignDisconnectError_DemotesToWarningPreservingContent()
    {
        var collectingSink = new CollectingSink();
        using var sink = CreateDemotingSink(collectingSink);
        var logEvent = CreateLogEvent(
            LogEventLevel.Error,
            new JSDisconnectedException("JavaScript interop calls cannot be issued at this time."),
            RemoteNavigationManagerSource,
            "Navigation failed when changing the location to {Uri}");

        sink.Emit(logEvent);

        Assert.That(collectingSink.Events, Has.Count.EqualTo(1));
        var written = collectingSink.Events.Single();
        Assert.That(written.Level, Is.EqualTo(LogEventLevel.Warning));
        Assert.That(written.MessageTemplate.Text, Is.EqualTo(logEvent.MessageTemplate.Text));
        Assert.That(written.Exception, Is.InstanceOf<JSDisconnectedException>());
        Assert.That(written.Properties["SourceContext"], Is.InstanceOf<ScalarValue>());
        Assert.That(((ScalarValue)written.Properties["SourceContext"]).Value, Is.EqualTo(RemoteNavigationManagerSource));
    }

    [Test]
    public void Emit_UnrelatedError_PassesThroughUnchanged()
    {
        var collectingSink = new CollectingSink();
        using var sink = CreateDemotingSink(collectingSink);
        var logEvent = CreateLogEvent(
            LogEventLevel.Error,
            new NullReferenceException("Object reference not set to an instance of an object."),
            CircuitHostSource,
            "Unhandled exception in circuit '{CircuitId}'.");

        sink.Emit(logEvent);

        Assert.That(collectingSink.Events, Has.Count.EqualTo(1));
        Assert.That(collectingSink.Events.Single(), Is.SameAs(logEvent));
    }

    [Test]
    public void Emit_InformationEvent_PassesThroughUnchanged()
    {
        var collectingSink = new CollectingSink();
        using var sink = CreateDemotingSink(collectingSink);
        var logEvent = CreateLogEvent(LogEventLevel.Information, null, "JIM.Web.Program");

        sink.Emit(logEvent);

        Assert.That(collectingSink.Events, Has.Count.EqualTo(1));
        Assert.That(collectingSink.Events.Single(), Is.SameAs(logEvent));
    }

    #endregion

    private static CircuitDisconnectDemotingSink CreateDemotingSink(ILogEventSink collectingSink)
    {
        var innerLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(collectingSink)
            .CreateLogger();
        return new CircuitDisconnectDemotingSink(innerLogger);
    }

    private static LogEvent CreateLogEvent(
        LogEventLevel level,
        Exception? exception,
        string? sourceContext,
        string messageTemplate = "Test message")
    {
        var properties = new List<LogEventProperty>();
        if (sourceContext != null)
            properties.Add(new LogEventProperty("SourceContext", new ScalarValue(sourceContext)));

        return new LogEvent(
            DateTimeOffset.UtcNow,
            level,
            exception,
            new MessageTemplateParser().Parse(messageTemplate),
            properties);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
