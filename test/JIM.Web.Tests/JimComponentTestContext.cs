// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using MudBlazor.Services;

namespace JIM.Web.Tests;

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
    /// <summary>
    /// How long a <c>WaitForElement</c> / <c>WaitForState</c> waits before failing.
    /// <para>
    /// bUnit's own default is one second, which is a measurement of the machine rather than of the component: a
    /// MudBlazor dialog's first render on a contended CI runner does not reliably finish inside it, and the
    /// failure arrives as an ordinary assertion failure naming a test that is perfectly correct. That is worse
    /// than a slow suite, because it teaches everyone to re-run rather than to read. Ten seconds is long enough
    /// that only a component genuinely never reaching the state can exhaust it, and costs nothing on a passing
    /// test, which stops waiting the moment its condition is met.
    /// </para>
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    protected JimComponentTestContext()
    {
        ConfigureAdditionalServices();
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        DefaultWaitTimeout = WaitTimeout;
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
