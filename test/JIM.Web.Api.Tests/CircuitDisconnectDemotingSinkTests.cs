// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Logging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    [Test]
    public async Task IsBenignCircuitDisconnectNoise_InteropTimeoutDuringComponentDisposal_ReturnsTrueAsync()
    {
        // A browser that stops answering (a frozen background tab) makes a component's disposal interop time out
        // rather than report a disconnect. The renderer reports it as an unhandled circuit exception, which killed a
        // 750k-object integration run on a portal tab nobody was looking at.
        var exception = await CaptureInteropTimeoutDuringDisposalAsync();
        var logEvent = CreateLogEvent(LogEventLevel.Error, exception, CircuitHostSource, "Unhandled exception in circuit '{CircuitId}'.");

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.True);
    }

    [Test]
    public async Task IsBenignCircuitDisconnectNoise_InteropTimeoutOutsideDisposal_ReturnsFalseAsync()
    {
        // Outside disposal a call that never completes can be a JavaScript function that never resolves, which is
        // a genuine fault, so only the teardown case is demoted.
        var exception = await CaptureAsync(() => new UnansweredJSRuntime().InvokeVoidAsync("jimTest.stop").AsTask());
        var logEvent = CreateLogEvent(LogEventLevel.Error, exception, CircuitHostSource, "Unhandled exception in circuit '{CircuitId}'.");

        Assert.That(CircuitDisconnectDemotingSink.IsBenignCircuitDisconnectNoise(logEvent), Is.False);
    }

    [Test]
    public async Task IsBenignCircuitDisconnectNoise_CancellationNotFromInterop_ReturnsFalseAsync()
    {
        var exception = await CaptureAsync(() => Task.Delay(Timeout.Infinite, new CancellationToken(canceled: true)));
        var logEvent = CreateLogEvent(LogEventLevel.Error, exception, CircuitHostSource, "Unhandled exception in circuit '{CircuitId}'.");

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

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (TaskCanceledException exception)
        {
            return exception;
        }

        throw new AssertionException("the action was expected to be cancelled");
    }

    /// <summary>
    /// Disposes a component whose disposal calls JavaScript that never answers, through the framework's own renderer,
    /// and returns what the renderer reported. The exception therefore carries the same frames as the one logged in a
    /// real circuit, rather than a hand-built approximation of them.
    /// </summary>
    private static async Task<Exception> CaptureInteropTimeoutDuringDisposalAsync()
    {
        using var renderer = new CapturingRenderer();
        var jsRuntime = new UnansweredJSRuntime();
        var host = new DisposalHost();

        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync(host, jsRuntime, showChild: true));
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync(host, jsRuntime, showChild: false));

        var reported = await Task.WhenAny(renderer.Reported, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.That(reported, Is.SameAs(renderer.Reported), "the renderer never reported the disposal failure");
        return await renderer.Reported;
    }

    private sealed class UnansweredJSRuntime : JSRuntime
    {
        public UnansweredJSRuntime()
        {
            DefaultAsyncTimeout = TimeSpan.FromMilliseconds(50);
        }

        protected override void BeginInvokeJS(long taskId, string identifier, string? argsJson, JSCallResultType resultType, long targetInstanceId)
        {
        }

        protected override void EndInvokeDotNet(DotNetInvocationInfo invocationInfo, in DotNetInvocationResult invocationResult)
        {
        }
    }

    // BL0006 warns that the renderer's types may change between releases. Using the real renderer is the point: a
    // change to how it reports disposal failures is exactly what this test should notice.
#pragma warning disable BL0006
    private sealed class CapturingRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        private readonly TaskCompletionSource<Exception> _reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int? _hostId;

        public Task<Exception> Reported => _reported.Task;

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task RenderAsync(DisposalHost host, IJSRuntime jsRuntime, bool showChild)
        {
            _hostId ??= AssignRootComponentId(host);
            return RenderRootComponentAsync(_hostId.Value, ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(DisposalHost.JS)] = jsRuntime,
                [nameof(DisposalHost.ShowChild)] = showChild
            }));
        }

        protected override void HandleException(Exception exception) => _reported.TrySetResult(exception);

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
#pragma warning restore BL0006

    private sealed class DisposalHost : ComponentBase
    {
        [Parameter] public IJSRuntime JS { get; set; } = null!;

        [Parameter] public bool ShowChild { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!ShowChild)
                return;

            builder.OpenComponent<DisposingChild>(0);
            builder.AddComponentParameter(1, nameof(DisposingChild.JS), JS);
            builder.CloseComponent();
        }
    }

    private sealed class DisposingChild : ComponentBase, IAsyncDisposable
    {
        [Parameter] public IJSRuntime JS { get; set; } = null!;

        public async ValueTask DisposeAsync() => await JS.InvokeVoidAsync("jimTest.stop");
    }
}
