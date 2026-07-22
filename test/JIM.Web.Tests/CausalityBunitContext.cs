// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using MudBlazor.Services;

namespace JIM.Web.Tests;

/// <summary>
/// Creates bUnit contexts configured for the causality components: MudBlazor services registered
/// (MudChip, MudAlert and friends property-inject them) and JS interop in loose mode so MudBlazor's
/// interop calls no-op.
/// </summary>
public static class CausalityBunitContext
{
    public static BunitContext Create()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        return context;
    }
}
