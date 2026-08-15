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
using Microsoft.AspNetCore.Components;
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
    public void VirtualisedDataGrid_ToolBar_AnchorsActionsLeftAndPutsTheCountBesideTheSearch()
    {
        // The toolbar has two anchored ends and nothing in between: the page's actions sit against the left edge
        // with the density toggle, and the count sits immediately left of the search box that changes it. A count
        // in the left slot pushed the primary action into open space in the middle, where it read as leftover.
        var cut = Render<VirtualisedDataGrid<string>>(parameters => parameters
            .Add(c => c.LoadWindow, (_, _) => Task.FromResult(new VirtualisedWindow<string>(new List<string> { "row" }, 4)))
            .Add(c => c.Columns, _ => { })
            .Add(c => c.ContainerId, "test-grid")
            .Add(c => c.DefaultSortBy, "Name")
            .Add(c => c.PluralName, "Widgets")
            .Add(c => c.ToolBarExtras, (RenderFragment)(builder => builder.AddMarkupContent(0, "<span>Create Widget</span>"))));

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            var action = markup.IndexOf("Create Widget", StringComparison.Ordinal);
            var count = markup.IndexOf("4 Widgets", StringComparison.Ordinal);
            var search = markup.IndexOf("placeholder=\"Search", StringComparison.Ordinal);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(action, Is.GreaterThan(-1), "the toolbar actions did not render");
                Assert.That(count, Is.GreaterThan(-1), "the count did not render");
                Assert.That(search, Is.GreaterThan(-1), "the search box did not render");
                Assert.That(action, Is.LessThan(count), "the page's actions must sit left of the count, against the density toggle");
                Assert.That(count, Is.LessThan(search), "the count must sit immediately left of the search box");
            }
        });
    }

    [Test]
    public void VirtualisedDataGrid_RowClassFunc_IsForwardedToTheUnderlyingGrid()
    {
        // Pages need per-row classes (e.g. dimming disabled Predefined Searches) without hand-rolling their own
        // grid; the wrapper forwards the delegate rather than exposing MudDataGrid directly.
        Func<string, int, string?> rowClass = (_, _) => "jim-readonly-row";

        var cut = Render<VirtualisedDataGrid<string>>(parameters => parameters
            .Add(c => c.LoadWindow, (_, _) => Task.FromResult(new VirtualisedWindow<string>(new List<string>(), 0)))
            .Add(c => c.Columns, _ => { })
            .Add(c => c.ContainerId, "test-grid")
            .Add(c => c.DefaultSortBy, "Name")
            .Add(c => c.RowClassFunc, rowClass));

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindComponent<MudBlazor.MudDataGrid<string>>().Instance.RowClassFunc,
                Is.SameAs(rowClass)));
    }

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
