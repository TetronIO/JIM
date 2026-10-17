// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using NUnit.Framework;

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

        // Any component holding a table now renders a VirtualisedDataGrid, which reads the saved row density,
        // so a preference service is part of the baseline rather than something each fixture remembers. TryAdd,
        // so a fixture that registered its own (to assert on what was written) keeps it.
        Services.TryAddSingleton<IUserPreferenceService, FakeUserPreferenceService>();
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

    /// <summary>
    /// Disposes every rendered component after each test. NUnit reuses a fixture instance across its tests, so a
    /// component left rendered (a polling loop, a delivery follow, a reveal timer) would otherwise run on into the next.
    /// </summary>
    [TearDown]
    public Task DisposeRenderedComponentsAfterTestAsync() => DisposeRenderedComponentsAsync();

    /// <summary>
    /// Disposes every rendered component. Use this, never bUnit's <c>DisposeComponentsAsync</c> on its own.
    /// <para>
    /// bUnit 2.11.3 queues the disposal on the renderer's dispatcher and then clears its list of root components on the
    /// calling thread. When a component's own continuation holds the dispatcher at that moment (a delay finishing just as
    /// the test ends), the queued disposal finds the list cleared, or is part-way through enumerating it, so components
    /// are never disposed or the teardown fails with "Collection was modified". Running the call on the dispatcher makes
    /// bUnit's inner dispatch run inline, so the enumeration finishes before the list is cleared. bUnit's main branch
    /// has fixed this by snapshotting the list on the dispatcher; once a release carries it, this can call bUnit directly.
    /// </para>
    /// </summary>
    protected Task DisposeRenderedComponentsAsync() => Renderer.Dispatcher.InvokeAsync(DisposeComponentsAsync);
}
