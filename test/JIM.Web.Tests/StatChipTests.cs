// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the outcome count chip on the Activity and Operations lists. When the list is crowded the chip shows its
/// icon alone, so the tooltip is the only place left to read the figure.
/// </summary>
[TestFixture]
public class StatChipTests : JimComponentTestContext
{
    [Test]
    public void StatChip_WithACount_TooltipCarriesTheExactFigure()
    {
        var cut = Render<StatChip>(p => p
            .Add(c => c.Label, "Metaverse Objects projected")
            .Add(c => c.Count, 524997)
            .Add(c => c.Icon, Icons.Material.Filled.Add));

        Assert.That(cut.FindComponent<MudTooltip>().Instance.Text, Is.EqualTo("Metaverse Objects projected: 524,997"));
    }

    [Test]
    public void StatChip_ZeroCount_TooltipIsTheLabelAlone()
    {
        // A zero chip stands for "nothing happened"; "No changes: 0" would say it twice.
        var cut = Render<StatChip>(p => p
            .Add(c => c.Label, "No changes")
            .Add(c => c.Count, 0)
            .Add(c => c.Icon, Icons.Material.Filled.Add));

        Assert.That(cut.FindComponent<MudTooltip>().Instance.Text, Is.EqualTo("No changes"));
    }

    [Test]
    public void StatChip_Count_RendersTheFigureInItsOwnHideableElement()
    {
        // The crowded list hides the figure and keeps the icon, so the figure must be separable from the chip.
        var cut = Render<StatChip>(p => p
            .Add(c => c.Label, "Connected System Objects added")
            .Add(c => c.Count, 1204)
            .Add(c => c.Icon, Icons.Material.Filled.Add));

        Assert.That(cut.Find(".jim-stat-chip-count").TextContent.Trim(), Is.EqualTo("1,204"));
    }
}
