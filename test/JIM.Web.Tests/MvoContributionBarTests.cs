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
            Assert.That(cut.Markup, Does.Contain("All 3 values from HR · HR Import"));
            Assert.That(cut.HasComponent<MudBlazor.MudText>(), Is.True);
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
