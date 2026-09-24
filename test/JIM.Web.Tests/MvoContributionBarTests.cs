// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core.DTOs;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Inspect view's contribution bar (#399): grouping attributes by their <see cref="ValueOrigin"/>,
/// the single-origin collapse to one sentence, and the legend doubling as the accessible source filter.
/// </summary>
[TestFixture]
public class MvoContributionBarTests : JimComponentTestContext
{
    private static ValueOrigin HrOrigin => new()
    {
        Kind = ValueOriginKind.SynchronisationRule,
        ConnectedSystemId = 1,
        ConnectedSystemName = "HR",
        SyncRuleId = 10,
        SyncRuleName = "HR Import"
    };

    private static ValueOrigin AdOrigin => new()
    {
        Kind = ValueOriginKind.SynchronisationRule,
        ConnectedSystemId = 2,
        ConnectedSystemName = "AD",
        SyncRuleId = 20,
        SyncRuleName = "AD Import"
    };

    private static MetaverseAttributeOriginSummary Attribute(int id, params ValueOrigin[] origins) => new()
    {
        AttributeId = id,
        AttributeName = $"attr{id}",
        Origins = origins.ToList()
    };

    [Test]
    public void ContributionBar_SameConnectedSystemThroughTwoRules_SharesOneColourDistinctFromAnotherSystem()
    {
        // Colours follow the Connected System, deterministically: string.GetHashCode() is randomised per process,
        // so a hash-derived colour changed on every restart and could differ between web instances (#399).
        var hrSecondRule = HrOrigin with { SyncRuleId = 11, SyncRuleName = "HR Contractors Import" };
        var provenance = new MetaverseObjectProvenance
        {
            Attributes = [Attribute(1, HrOrigin), Attribute(2, hrSecondRule), Attribute(3, AdOrigin)]
        };

        var cut = Render<MvoContributionBar>(p => p
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ObjectTypeName, "User"));

        var swatches = cut.FindAll(".jim-contribution-legend-swatch").Select(s => s.GetAttribute("style")).ToList();
        var labels = cut.FindAll("button[aria-pressed]").Select(b => b.TextContent).ToList();
        var hr = swatches[labels.FindIndex(l => l.Contains("HR Import"))];
        var hrContractors = swatches[labels.FindIndex(l => l.Contains("HR Contractors Import"))];
        var ad = swatches[labels.FindIndex(l => l.Contains("AD"))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hrContractors, Is.EqualTo(hr));
            Assert.That(ad, Is.Not.EqualTo(hr));
        }
    }

    [Test]
    public void ContributionBar_FirstConnectedSystem_TakesTheInfoColourItsSystemChipWears()
    {
        // Connected System ids start at 1, and the Connected System chip's glyph wears the info colour, so the
        // first system's segment matches the chips beside it in the table rather than a colour of its own.
        var first = HrOrigin with { ConnectedSystemId = 1 };
        var second = AdOrigin with { ConnectedSystemId = 2 };
        var provenance = new MetaverseObjectProvenance { Attributes = [Attribute(1, first), Attribute(2, second)] };

        var cut = Render<MvoContributionBar>(p => p
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ObjectTypeName, "User"));

        var swatches = cut.FindAll(".jim-contribution-legend-swatch").Select(s => s.GetAttribute("style")).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(swatches[0], Does.Contain("var(--mud-palette-info)"));
            Assert.That(swatches[1], Does.Contain("var(--mud-palette-success)"));
        }
    }

    [Test]
    public void ContributionBar_GeneratedByJim_TakesThePrimaryColour()
    {
        var generated = new ValueOrigin { Kind = ValueOriginKind.GeneratedByJim, ConnectedSystemId = 1, ConnectedSystemName = "HR", SyncRuleId = 10, SyncRuleName = "HR Import" };
        var provenance = new MetaverseObjectProvenance { Attributes = [Attribute(1, generated), Attribute(2, AdOrigin)] };

        var cut = Render<MvoContributionBar>(p => p
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ObjectTypeName, "User"));

        Assert.That(cut.Markup, Does.Contain("var(--mud-palette-primary)"));
    }

    [Test]
    public void ContributionBar_AllAttributesFromOneSource_CollapsesToOneSentence()
    {
        var provenance = new MetaverseObjectProvenance
        {
            Attributes = [Attribute(1, HrOrigin), Attribute(2, HrOrigin), Attribute(3, HrOrigin)]
        };

        var cut = Render<MvoContributionBar>(p => p
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ObjectTypeName, "User"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Where this User gets its values"));
            Assert.That(cut.Markup, Does.Contain("3 attributes"));
            Assert.That(cut.Find(".jim-contribution-single").TextContent.Trim(), Is.EqualTo("All 3 values from HR · HR Import"));
            Assert.That(cut.FindAll(".jim-contribution-bar"), Is.Empty, "one source needs no bar");
            Assert.That(cut.FindAll(".jim-contribution-legend-item"), Is.Empty, "one source needs no legend to filter by");
        }
    }

    [Test]
    public void ContributionBar_SeveralSources_RendersASegmentAndLegendItemPerGroupWithCounts()
    {
        var provenance = new MetaverseObjectProvenance
        {
            Attributes =
            [
                Attribute(1, HrOrigin), Attribute(2, HrOrigin),
                Attribute(3, AdOrigin),
                Attribute(4, ValueOrigin.NotRecorded)
            ]
        };

        var cut = Render<MvoContributionBar>(p => p
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ObjectTypeName, "User"));

        var legendItems = cut.FindAll(".jim-contribution-legend-item");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".jim-contribution-bar-segment"), Has.Count.EqualTo(3));
            Assert.That(legendItems, Has.Count.EqualTo(3));
            // Each system contributes through exactly one rule here, so the compact legend label names only the
            // Connected System; ContributionBar_SameSystemThroughTwoRules below covers the disambiguating case.
            Assert.That(legendItems.Select(i => i.TextContent), Has.Some.Contains("HR"));
            Assert.That(legendItems.Select(i => i.TextContent), Has.Some.Contains("AD"));
            Assert.That(cut.Markup, Does.Not.Contain("HR Import"));
            Assert.That(cut.Markup, Does.Contain("Source not recorded"));
            // The HR group (2 attributes) sorts first: BuildGroups orders by count descending.
            Assert.That(legendItems[0].TextContent, Does.Contain("2"));
        }
    }

    [Test]
    public void ContributionBar_SameSystemThroughTwoRules_NamesTheRuleInBothLegendItems()
    {
        var secondHrRule = HrOrigin with { SyncRuleId = 11, SyncRuleName = "HR Contractors Import" };
        var provenance = new MetaverseObjectProvenance
        {
            Attributes = [Attribute(1, HrOrigin), Attribute(2, secondHrRule)]
        };

        var cut = Render<MvoContributionBar>(p => p
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ObjectTypeName, "User"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("HR · HR Import"));
            Assert.That(cut.Markup, Does.Contain("HR · HR Contractors Import"));
        }
    }

    [Test]
    public void ContributionBar_AttributeWithSeveralOrigins_FormsItsOwnSegment()
    {
        var provenance = new MetaverseObjectProvenance
        {
            Attributes =
            [
                Attribute(1, HrOrigin),
                Attribute(2, HrOrigin, AdOrigin) // several origins on one attribute
            ]
        };

        var cut = Render<MvoContributionBar>(p => p
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ObjectTypeName, "User"));

        Assert.That(cut.Markup, Does.Contain("Several sources"));
    }

    [Test]
    public void ContributionBar_LegendItemClicked_RaisesOnFilterChangedWithTheGroupKey()
    {
        var provenance = new MetaverseObjectProvenance
        {
            Attributes = [Attribute(1, HrOrigin), Attribute(2, AdOrigin)]
        };

        string? raised = "not called";
        var cut = Render<MvoContributionBar>(p => p
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.OnFilterChanged, EventCallback.Factory.Create<string?>(this, key => raised = key)));

        cut.FindAll(".jim-contribution-legend-item")[0].Click();

        Assert.That(raised, Is.EqualTo(ValueOriginGrouping.KeyFor(Attribute(1, HrOrigin))));
    }

    [Test]
    public void ContributionBar_ActiveFilterClickedAgain_RaisesNullToClearIt()
    {
        var provenance = new MetaverseObjectProvenance
        {
            Attributes = [Attribute(1, HrOrigin), Attribute(2, AdOrigin)]
        };
        var activeKey = ValueOriginGrouping.KeyFor(Attribute(1, HrOrigin));

        string? raised = "not called";
        var cut = Render<MvoContributionBar>(p => p
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.ActiveFilterKey, activeKey)
            .Add(c => c.OnFilterChanged, EventCallback.Factory.Create<string?>(this, key => raised = key)));

        var pressedItem = cut.FindAll(".jim-contribution-legend-item")
            .First(e => e.GetAttribute("aria-pressed") == "true");
        pressedItem.Click();

        Assert.That(raised, Is.Null);
    }
}
