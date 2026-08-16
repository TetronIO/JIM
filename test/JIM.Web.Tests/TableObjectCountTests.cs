// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The object count that sits in a table toolbar's title slot. The virtualised Metaverse Object list has no pager,
/// so this is the only place the total match count appears; the round trip it has to get right is "count alone"
/// versus "x of y" while a filter narrows the list, and rendering nothing at all until a count exists.
/// </summary>
[TestFixture]
public class TableObjectCountTests : JimComponentTestContext
{
    private IRenderedComponent<TableObjectCount> RenderCount(
        int? count, int? total = null, string? singular = null, string? plural = null, bool showSeparator = false)
    {
        return Render<TableObjectCount>(p => p
            .Add(c => c.Count, count)
            .Add(c => c.Total, total)
            .Add(c => c.SingularName, singular)
            .Add(c => c.PluralName, plural)
            .Add(c => c.ShowSeparator, showSeparator));
    }

    [Test]
    public void TableObjectCount_NoCountYet_RendersNothing()
    {
        // Null is the loading state: the count appears once the first window of data has arrived,
        // rather than flashing a zero that means "not counted".
        var cut = RenderCount(count: null, showSeparator: true);

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void TableObjectCount_CountWithPluralNoun_RendersCountAndNoun()
    {
        var cut = RenderCount(3868, plural: "Service Principals", singular: "Service Principal");

        Assert.That(cut.Markup, Does.Contain("3,868 Service Principals"));
    }

    [Test]
    public void TableObjectCount_CountOfOne_UsesTheSingularNoun()
    {
        var cut = RenderCount(1, plural: "Service Principals", singular: "Service Principal");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("1 Service Principal"));
            Assert.That(cut.Markup, Does.Not.Contain("Service Principals"));
        }
    }

    [Test]
    public void TableObjectCount_FilteredBelowTheTotal_RendersXOfY()
    {
        var cut = RenderCount(12, total: 3868, plural: "Service Principals");

        Assert.That(cut.Markup, Does.Contain("12 of 3,868 Service Principals"));
    }

    [Test]
    public void TableObjectCount_CountEqualsTheTotal_CollapsesToTheCountAlone()
    {
        // "3,868 of 3,868" restates itself; an unfiltered list reads as a plain total.
        var cut = RenderCount(3868, total: 3868, plural: "Service Principals");

        Assert.That(cut.Markup, Does.Not.Contain("of"));
    }

    [Test]
    public void TableObjectCount_NoNounGiven_RendersTheBareNumber()
    {
        // Beside a toolbar title the noun is already on screen; the count must not restate it.
        var cut = RenderCount(1204);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("1,204"));
            Assert.That(cut.Instance.PluralName, Is.Null);
        }
    }

    [Test]
    public void TableObjectCount_SeparatorRequested_RendersTheStandardToolbarSeparator()
    {
        var cut = RenderCount(10, plural: "Connectors", showSeparator: true);

        Assert.That(cut.Markup, Does.Contain("|"));
    }

    [Test]
    public void TableObjectCount_SeparatorRequestedButNoCountYet_RendersNoDanglingSeparator()
    {
        // The separator belongs to the count; while the count is unknown neither may appear,
        // or the toolbar shows a bare "|" during the first load.
        var cut = RenderCount(count: null, plural: "Connectors", showSeparator: true);

        Assert.That(cut.Markup, Does.Not.Contain("|"));
    }
}
