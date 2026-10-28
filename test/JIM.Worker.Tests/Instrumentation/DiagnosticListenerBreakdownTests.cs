// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Diagnostics;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Worker.Tests.Instrumentation;

/// <summary>
/// A slow span logs where its time went, totalled over descendants too quick to be logged one by one (#2492).
/// </summary>
[TestFixture]
public class DiagnosticListenerBreakdownTests
{
    private const double ThresholdMs = 40;

    [Test]
    public void SpanStopped_SlowSpanWithQuickDescendants_LogsTheirTotalsByPath()
    {
        var messages = CaptureListenerMessages(() =>
        {
            using (Diagnostics.Sync.StartSpan("BreakdownTestParent"))
            {
                for (var i = 0; i < 3; i++)
                {
                    using (Diagnostics.Sync.StartSpan("BreakdownTestChild"))
                    {
                        using (Diagnostics.Sync.StartSpan("BreakdownTestGrandchild"))
                        {
                        }
                    }
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(ThresholdMs + 20));
            }
        });

        // The assembly's own listener (GlobalTestSetup) logs one too; each counts only its own, so either will do.
        var breakdown = messages.FirstOrDefault(m => m.Contains("BreakdownTestParent breakdown:"));
        Assert.That(breakdown, Is.Not.Null, string.Join(Environment.NewLine, messages));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(breakdown, Does.Contain("BreakdownTestChild 3x"));
            Assert.That(breakdown, Does.Contain("BreakdownTestChild/BreakdownTestGrandchild 3x"));
            Assert.That(breakdown, Does.Contain("unaccounted"));
            Assert.That(breakdown, Does.Not.Contain("completed in"),
                "The integration runner and the benchmark parser read span timings from 'completed in' lines; a breakdown must not look like one.");
        }
    }

    [Test]
    public void SpanStopped_QuickSpan_LogsNoBreakdown()
    {
        var messages = CaptureListenerMessages(() =>
        {
            using (Diagnostics.Sync.StartSpan("BreakdownTestQuickParent"))
            {
                using (Diagnostics.Sync.StartSpan("BreakdownTestChild"))
                {
                }
            }
        });

        Assert.That(messages.Any(m => m.Contains("BreakdownTestQuickParent breakdown:")), Is.False);
    }

    [Test]
    public void SpanStopped_SlowSpanWithNoDescendants_LogsNoBreakdown()
    {
        var messages = CaptureListenerMessages(() =>
        {
            using (Diagnostics.Sync.StartSpan("BreakdownTestLeaf"))
                Thread.Sleep(TimeSpan.FromMilliseconds(ThresholdMs + 20));
        });

        Assert.That(messages.Any(m => m.Contains("BreakdownTestLeaf breakdown:")), Is.False);
    }

    private static List<string> CaptureListenerMessages(Action action)
    {
        var sink = new CapturingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            using (new DiagnosticListener(slowOperationThresholdMs: ThresholdMs))
                action();
        }
        finally
        {
            Log.Logger = original;
        }

        return sink.Events.Select(e => e.RenderMessage()).ToList();
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public List<LogEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }
}
