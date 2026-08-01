// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using MudBlazor.Services;

namespace JIM.Web.Components.Tests;

/// <summary>
/// Base class for JIM.Web component tests. Registers the MudBlazor services every JIM component
/// renders against and puts bUnit's JS interop in loose mode, so components that call into
/// JavaScript (MudBlazor does so widely, for popovers, resize observers and scroll listeners)
/// render instead of throwing on an unconfigured invocation.
///
/// Assert on JIM's own markup and component state, never on MudBlazor's generated CSS class names:
/// those are a third party's implementation detail and change between MudBlazor releases, which
/// would turn this suite into upgrade friction rather than a safety net.
/// </summary>
public abstract class JimComponentTestContext : BunitContext
{
    protected JimComponentTestContext()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
}
