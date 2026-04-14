// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace JIM.TestSupport;

/// <summary>
/// Assembly-level NUnit action attribute that records a process memory snapshot before and
/// after every test in the assembly. Opt-in via the <c>JIM_TEST_MEMORY_LOG</c> environment
/// variable — unset means no file is written and both hooks are near-zero-cost.
///
/// Apply by adding this single line anywhere in the test assembly (typically next to the
/// existing <c>[SetUpFixture]</c>):
/// <code>[assembly: JIM.TestSupport.MemoryLogging]</code>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class MemoryLoggingAttribute : TestActionAttribute
{
    public override ActionTargets Targets => ActionTargets.Test;

    public override void BeforeTest(ITest test)
    {
        MemoryDiagnostics.WriteSnapshot(ResolveAssemblyName(test), test.FullName, "before");
    }

    public override void AfterTest(ITest test)
    {
        MemoryDiagnostics.WriteSnapshot(ResolveAssemblyName(test), test.FullName, "after");
    }

    private static string ResolveAssemblyName(ITest test)
    {
        return test.TypeInfo?.Assembly.GetName().Name
            ?? MemoryDiagnostics.GetEntryAssemblyName();
    }
}
