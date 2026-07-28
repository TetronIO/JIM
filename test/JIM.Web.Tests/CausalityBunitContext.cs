// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;

namespace JIM.Web.Tests;

/// <summary>
/// Creates bUnit contexts for the causality component tests. The configuration (MudBlazor services,
/// loose JS interop, and the assertion hygiene rules that go with them) is defined once on
/// <see cref="JimComponentTestContext"/>; this factory exists for test methods that prefer a
/// disposable local context over fixture inheritance.
/// </summary>
public static class CausalityBunitContext
{
    private sealed class Context : JimComponentTestContext;

    public static BunitContext Create()
    {
        return new Context();
    }
}
