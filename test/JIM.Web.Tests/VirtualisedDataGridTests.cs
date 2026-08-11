// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using JIM.Web.Models;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The virtualised grid sizes itself against the page footer at runtime (jimVirtualList.fit) rather than
/// carrying a hand-tuned height per page; these tests pin the interop contract that makes every list end at
/// the same place: the fit is requested once the grid is in the DOM, and released when the grid goes away.
/// They also pin the StateKey reuse contract: SPA navigation between two views sharing a route template
/// reuses the component instance, and the grid must reload its data rather than keep showing the old view's rows.
/// </summary>
[TestFixture]
public class VirtualisedDataGridTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
    }

    private IRenderedComponent<VirtualisedDataGrid<string>> RenderGrid(
        Func<VirtualisedWindowRequest, CancellationToken, Task<VirtualisedWindow<string>>>? loadWindow = null,
        string stateKey = "")
        => Render<VirtualisedDataGrid<string>>(parameters => parameters
            .Add(c => c.LoadWindow, loadWindow ?? ((_, _) => Task.FromResult(new VirtualisedWindow<string>(new List<string>(), 0))))
            .Add(c => c.Columns, _ => { })
            .Add(c => c.ContainerId, "test-grid")
            .Add(c => c.DefaultSortBy, "Name")
            .Add(c => c.StateKey, stateKey));

    [Test]
    public void VirtualisedDataGrid_OnceRendered_FitsItsHeightToTheViewport()
    {
        var cut = RenderGrid();

        cut.WaitForAssertion(() =>
        {
            var fit = JSInterop.Invocations.FirstOrDefault(i => i.Identifier == "jimVirtualList.fit");
            Assert.That(fit, Is.Not.Null, "the grid never asked JavaScript to fit its height");
            Assert.That(fit!.Arguments[0], Is.EqualTo("#test-grid .mud-table-container"));
        });
    }

    [Test]
    public void VirtualisedDataGrid_StateKeyChangedOnReusedInstance_ReloadsItsData()
    {
        // SPA navigation between two views sharing a route template (e.g. /activity and /activity/mine)
        // reuses the component instance; the changed StateKey is the only signal the view identity changed.
        // Without a reload the virtualiser keeps serving the previous view's cached rows.
        var loadCount = 0;
        var cut = RenderGrid(loadWindow: (_, _) =>
        {
            Interlocked.Increment(ref loadCount);
            return Task.FromResult(new VirtualisedWindow<string>(new List<string> { "row" }, 1));
        }, stateKey: "/activity");

        cut.WaitForAssertion(() => Assert.That(loadCount, Is.GreaterThan(0),
            "the grid never loaded its initial window"));
        var loadsBeforeNavigation = loadCount;

        cut.Render(parameters => parameters.Add(c => c.StateKey, "/activity/mine"));

        cut.WaitForAssertion(() => Assert.That(loadCount, Is.GreaterThan(loadsBeforeNavigation),
            "a StateKey change on a reused instance must reload the grid's data; the rows on screen belong to the previous view"));
    }

    [Test]
    public void VirtualisedDataGrid_StateKeyChangedOnReusedInstance_ReturnsTheReaderToTheTop()
    {
        // The DOM survives the navigation too, so the previous view's scroll offset would otherwise be
        // inherited by the new view; the grid must scroll back to the row the (new) URL asks for.
        var cut = RenderGrid(stateKey: "/activity");
        cut.WaitForAssertion(() =>
            Assert.That(JSInterop.Invocations.Any(i => i.Identifier == "jimVirtualList.fit"), Is.True));
        var scrollsBeforeNavigation = JSInterop.Invocations.Count(i => i.Identifier == "jimVirtualList.scrollToRow");

        cut.Render(parameters => parameters.Add(c => c.StateKey, "/activity/mine"));

        cut.WaitForAssertion(() =>
            Assert.That(JSInterop.Invocations.Count(i => i.Identifier == "jimVirtualList.scrollToRow"),
                Is.GreaterThan(scrollsBeforeNavigation),
                "a StateKey change on a reused instance must restore the scroll position for the new view"));
    }

    [Test]
    public async Task VirtualisedDataGrid_WhenDisposed_ReleasesItsViewportFit()
    {
        var cut = RenderGrid();
        cut.WaitForAssertion(() =>
            Assert.That(JSInterop.Invocations.Any(i => i.Identifier == "jimVirtualList.fit"), Is.True));

        await DisposeComponentsAsync();

        Assert.That(JSInterop.Invocations.Any(i => i.Identifier == "jimVirtualList.unfit"), Is.True,
            "disposal must release the resize listener the fit registered");
    }
}
