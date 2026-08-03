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
        ConfigureAdditionalServices();
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Override to register test-specific services (e.g. a fake <c>IJimApplicationFactory</c>) on
    /// <see cref="BunitContext.Services"/> before the base MudBlazor services are added.
    /// <para>
    /// This cannot be done from a derived fixture's <c>[SetUp]</c>: bUnit locks the service provider against
    /// further registration once a service has been resolved from it, and <c>AddMudServices()</c> /
    /// <c>JSInterop.Mode</c> below both do so as part of this base constructor, which always runs before
    /// <c>[SetUp]</c>. Because this runs from within the base constructor, it executes before the derived
    /// class's own field initializers, so build whatever is registered here entirely inside the override
    /// rather than relying on derived-class fields.
    /// </para>
    /// </summary>
    protected virtual void ConfigureAdditionalServices()
    {
    }
}
