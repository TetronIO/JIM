// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Application.Diagnostics;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Global test setup that runs once for all tests in this assembly.
/// Enables performance diagnostics for all tests.
/// </summary>
[SetUpFixture]
public class GlobalTestSetup
{
    private static DiagnosticListener? _diagnosticListener;

    [OneTimeSetUp]
    public void GlobalSetup()
    {
        // Enable diagnostics with a 50ms threshold for tests (lower than production 100ms)
        // This helps identify even moderately slow operations during testing
        _diagnosticListener = Diagnostics.EnableLogging(slowOperationThresholdMs: 50);

        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("🔍 Performance Diagnostics ENABLED for JIM.Web.Api.Tests");
        Console.WriteLine("   Slow operation threshold: 50ms");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
    }

    [OneTimeTearDown]
    public void GlobalTearDown()
    {
        _diagnosticListener?.Dispose();
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("🔍 Performance Diagnostics DISABLED");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
    }
}
