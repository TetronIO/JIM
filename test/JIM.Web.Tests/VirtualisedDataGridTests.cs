// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
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
/// </summary>
[TestFixture]
public class VirtualisedDataGridTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
    }

    private IRenderedComponent<VirtualisedDataGrid<string>> RenderGrid()
        => Render<VirtualisedDataGrid<string>>(parameters => parameters
            .Add(c => c.LoadWindow, (_, _) => Task.FromResult(new VirtualisedWindow<string>(new List<string>(), 0)))
            .Add(c => c.Columns, _ => { })
            .Add(c => c.ContainerId, "test-grid")
            .Add(c => c.DefaultSortBy, "Name"));

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
