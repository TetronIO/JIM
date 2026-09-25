// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Utility;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Inspect view's attribute table (#399): rendering from provenance handed down by the host (it is
/// presentational, taking no dependency on the application layer), rows opening the inspector, and Group by
/// Source hiding the Source column since its header states the source instead.
/// </summary>
[TestFixture]
public class MvoDetailsTableTests : JimComponentTestContext
{
    private Mock<IMetaverseRepository> _metaverse = null!;

    /// <summary>
    /// Registers a fake application factory so a multi-valued attribute over the inline threshold can render
    /// its <see cref="MvoMvaTable"/> fallback, which injects <c>IJimApplicationFactory</c> for its own window
    /// reads (pattern: <c>MvoMvaTableTests</c>). Every other test in this fixture ignores it.
    /// </summary>
    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }

    private static MetaverseObjectAttributeValue TextValue(int attributeId, string name, string value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = new MetaverseAttribute { Id = attributeId, Name = name, Type = AttributeDataType.Text },
        StringValue = value
    };

    private static MetaverseObject BuildObject(params MetaverseObjectAttributeValue[] values) => new()
    {
        Id = Guid.NewGuid(),
        Type = new MetaverseObjectType { Name = "User", PluralName = "Users" },
        AttributeValues = values.ToList()
    };

    private static MetaverseObjectProvenance BuildProvenance(params (int Id, string Name, ValueOrigin Origin)[] attrs) => new()
    {
        Attributes = attrs.Select(a => new MetaverseAttributeOriginSummary
        {
            AttributeId = a.Id,
            AttributeName = a.Name,
            Origins = [a.Origin]
        }).ToList()
    };

    private static ValueOrigin HrOrigin => new()
    {
        Kind = ValueOriginKind.SynchronisationRule,
        ConnectedSystemId = 1,
        ConnectedSystemName = "HR",
        SyncRuleId = 5,
        SyncRuleName = "HR Import"
    };

    [Test]
    public void DetailsTable_WithProvenance_RendersASourceColumnNamingEachSourceWithTheBarsDotAndLabel()
    {
        // Two sources, so the contribution bar draws a legend to compare the rows' dots against.
        var directory = HrOrigin with { ConnectedSystemId = 2, ConnectedSystemName = "Directory", SyncRuleId = 6, SyncRuleName = "Directory Import" };
        var mvo = BuildObject(TextValue(1, "Job Title", "Engineer"), TextValue(2, "Email", "e@example.com"));
        var provenance = BuildProvenance((1, "Job Title", HrOrigin), (2, "Email", directory));

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.Provenance, provenance));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("th:nth-child(3)").TextContent, Is.EqualTo("Source"));
            // The same group, label and colour the contribution bar's legend uses, so a row can be read against it.
            var legend = cut.FindAll(".jim-contribution-legend-item")
                .ToDictionary(b => b.QuerySelector("span:nth-child(2)")!.TextContent, b => b.QuerySelector(".jim-contribution-legend-swatch")!.GetAttribute("style"));
            var hrRow = cut.FindAll("tr.jim-inspect-row").Single(r => r.TextContent.Contains("Job Title"));
            Assert.That(hrRow.QuerySelector(".jim-source-dot")!.GetAttribute("style"), Is.EqualTo(legend["HR"]));
            Assert.That(hrRow.QuerySelector(".jim-source-dot-name")!.TextContent, Is.EqualTo("HR"));
            Assert.That(hrRow.QuerySelector(".jim-source-dot-rule")!.TextContent, Is.EqualTo("HR Import"),
                "with the inspector closed there is room to name the rule");
        }
    }

    [Test]
    public void DetailsTable_RowClicked_RaisesOnAttributeSelectedWithTheAttributeId()
    {
        var mvo = BuildObject(TextValue(7, "Job Title", "Engineer"));
        int? selected = null;

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.OnAttributeSelected, EventCallback.Factory.Create<int>(this, id => selected = id)));

        cut.Find("tr.jim-inspect-row").Click();

        Assert.That(selected, Is.EqualTo(7));
    }

    [Test]
    public void DetailsTable_AttributeNameButton_ClickedRaisesOnAttributeSelected()
    {
        var mvo = BuildObject(TextValue(7, "Job Title", "Engineer"));
        int? selected = null;

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.OnAttributeSelected, EventCallback.Factory.Create<int>(this, id => selected = id)));

        cut.Find("button.jim-attr-name-button").Click();

        Assert.That(selected, Is.EqualTo(7));
    }

    [Test]
    public void DetailsTable_AttributeNameButton_AriaExpandedReflectsWhetherItsInspectorIsOpen()
    {
        var mvo = BuildObject(TextValue(7, "Job Title", "Engineer"));

        var closedCut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User"));
        var openCut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.SelectedAttributeId, 7));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(closedCut.Find("button.jim-attr-name-button").GetAttribute("aria-expanded"), Is.EqualTo("false"));
            Assert.That(openCut.Find("button.jim-attr-name-button").GetAttribute("aria-expanded"), Is.EqualTo("true"));
        }
    }

    [Test]
    public void DetailsTable_SourceCell_HasNoLinksAndOpensTheInspectorWhenClicked()
    {
        var mvo = BuildObject(TextValue(1, "Job Title", "Engineer"));
        var provenance = BuildProvenance((1, "Job Title", HrOrigin));
        int? selected = null;

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.Provenance, provenance)
            .Add(c => c.OnAttributeSelected, EventCallback.Factory.Create<int>(this, id => selected = id)));

        var sourceCell = cut.Find(".jim-inspect-source-cell");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceCell.QuerySelector(".jim-source-dot"), Is.Not.Null);
            Assert.That(sourceCell.QuerySelectorAll("a"), Is.Empty, "the row's source leaves its links to the inspector");
        }

        sourceCell.Click();

        Assert.That(selected, Is.EqualTo(1), "a click on the linkless Source cell opens the inspector like the rest of the row");
    }

    [Test]
    public void DetailsTable_GroupBySegmentedControl_RaisesOnGroupByChangedWithTheChosenOption()
    {
        var mvo = BuildObject(TextValue(1, "Job Title", "Engineer"));
        string? raised = null;

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.GroupBy, "none")
            .Add(c => c.OnGroupByChanged, EventCallback.Factory.Create<string>(this, g => raised = g)));

        var control = cut.FindComponent<SegmentedControl<string>>();
        cut.FindAll(".jim-segmented button").Single(b => b.TextContent.Trim() == "Category").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(control.Instance.AriaLabel, Is.EqualTo("Group by"));
            Assert.That(raised, Is.EqualTo("category"));
        }
    }

    [Test]
    public void DetailsTable_SingleTextValue_RendersOnOneClippedLineWithItsFullTextAsTheTitle()
    {
        const string longValue = "Principal Software Engineer, Identity and Access Management Platform";
        var mvo = BuildObject(TextValue(1, "Job Title", longValue));

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User"));

        var value = cut.Find("td.jim-attr-value > div");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.ClassList, Does.Contain("jim-inspect-value"));
            Assert.That(value.GetAttribute("title"), Is.EqualTo(longValue));
        }
    }

    [Test]
    public void DetailsTable_SelectedAttribute_HighlightsItsRowAndHidesTheTypeAndPluralityColumns()
    {
        var mvo = BuildObject(TextValue(7, "Job Title", "Engineer"));

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.Provenance, BuildProvenance((7, "Job Title", HrOrigin)))
            .Add(c => c.SelectedAttributeId, 7));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("tr.jim-inspect-row").ClassList, Does.Contain("jim-inspect-row-selected"));
            Assert.That(cut.FindAll("th").Select(h => h.TextContent), Does.Not.Contain("Plurality"));
            Assert.That(cut.FindAll("th").Select(h => h.TextContent), Does.Not.Contain("Type"));
            Assert.That(cut.FindAll("td.jim-attr-type"), Is.Empty);
            Assert.That(cut.FindAll(".jim-source-dot-rule"), Is.Empty, "the narrowed Source column names the system only");
        }
    }

    [Test]
    public void DetailsTable_GroupedBySource_HidesTheSourceColumnAndRendersASourceHeaderRow()
    {
        var mvo = BuildObject(TextValue(1, "Job Title", "Engineer"), TextValue(2, "Department", "Engineering"));
        var provenance = BuildProvenance((1, "Job Title", HrOrigin), (2, "Department", HrOrigin));

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.Provenance, provenance)
            .Add(c => c.GroupBy, "source"));

        var headerRow = cut.Find("tr.jim-inspect-group-header");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("th").Select(h => h.TextContent), Does.Not.Contain("Source"));
            // The header names the source with the same system-and-rule pill as the Source column it replaces,
            // not a hand-built label string.
            Assert.That(headerRow.QuerySelectorAll(".jim-system-rule-chip .jim-object-chip-name").Select(n => n.TextContent),
                Is.EqualTo(new[] { "HR", "HR Import" }));
            Assert.That(headerRow.TextContent, Does.Contain("2 attributes"));
            Assert.That(cut.FindAll("tr.jim-inspect-row"), Has.Count.EqualTo(2));
        }
    }

    [TestCase("source", 7)]
    [TestCase("source", null)]
    [TestCase("category", 7)]
    [TestCase("category", null)]
    public void DetailsTable_GroupHeaderRow_SpansExactlyTheColumnsTheTableHas(string groupBy, int? selectedAttributeId)
    {
        // A colspan wider than the header row makes the browser invent empty columns, which render as a dead band
        // down the right of the table (worst with the inspector open, where the table lays out at fixed widths).
        var mvo = BuildObject(TextValue(7, "Job Title", "Engineer"));
        var provenance = BuildProvenance((7, "Job Title", HrOrigin));

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.Provenance, provenance)
            .Add(c => c.GroupBy, groupBy)
            .Add(c => c.SelectedAttributeId, selectedAttributeId));

        var headerCount = cut.FindAll("thead th").Count;
        var span = cut.Find("tr.jim-inspect-group-header td").GetAttribute("colspan");

        Assert.That(span, Is.EqualTo(headerCount.ToString()));
    }

    [Test]
    public void DetailsTable_GroupedByCategory_RendersACategoryHeaderRow()
    {
        var mvo = BuildObject(TextValue(1, Constants.BuiltInAttributes.DisplayName, "Amelia Sullivan"));

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.GroupBy, "category"));

        Assert.That(cut.Find("tr.jim-inspect-group-header").TextContent, Does.Contain("Identity"));
    }

    [Test]
    public void DetailsTable_ContributionFilterActive_ShowsOnlyMatchingAttributes()
    {
        var mvo = BuildObject(TextValue(1, "Job Title", "Engineer"), TextValue(2, "Office", "London"));
        var provenance = BuildProvenance(
            (1, "Job Title", HrOrigin),
            (2, "Office", ValueOrigin.NotRecorded));

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ContributionFilterKey, ValueOriginGrouping.KeyFor(provenance.Attributes[0])));

        var rows = cut.FindAll("tr.jim-inspect-row");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].TextContent, Does.Contain("Job Title"));
        }
    }

    [Test]
    public void DetailsTable_ContributionFilterActive_PassesTheFilterKeyValueToTheContributionBar()
    {
        // Regression: a string parameter bound without @ passes the literal field name, not its value, so the
        // bar's legend never showed the active filter as pressed. Found by runtime verification, not bUnit (#399).
        var mvo = BuildObject(TextValue(1, "Job Title", "Engineer"), TextValue(2, "Office", "London"));
        var provenance = BuildProvenance(
            (1, "Job Title", HrOrigin),
            (2, "Office", ValueOrigin.NotRecorded));
        var filterKey = ValueOriginGrouping.KeyFor(provenance.Attributes[0]);

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.Provenance, provenance)
            .Add(c => c.ContributionFilterKey, filterKey));

        Assert.That(cut.FindComponent<MvoContributionBar>().Instance.ActiveFilterKey, Is.EqualTo(filterKey));
    }

    private static MetaverseObjectAttributeValue MvaValue(string value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = new MetaverseAttribute
        {
            Id = 3, Name = "Other Mobiles", Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued
        },
        StringValue = value
    };

    [Test]
    public void DetailsTable_MultiValuedAttributeWithinInlineThreshold_RendersTheStackedExpandedList()
    {
        var mvo = BuildObject(MvaValue("+44 1"), MvaValue("+44 2"));

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".jim-attr-expanded-item"), Has.Count.EqualTo(2));
            Assert.That(cut.Markup, Does.Contain("+44 1"));
            Assert.That(cut.Markup, Does.Contain("+44 2"));
            Assert.That(cut.HasComponent<MvoMvaTable>(), Is.False);
        }
    }

    [Test]
    public void DetailsTable_MultiValuedAttributeAboveInlineThreshold_RendersMvoMvaTable()
    {
        var values = Enumerable.Range(0, 11).Select(i => MvaValue($"+44 {i}")).ToArray();
        var mvo = BuildObject(values);
        _metaverse
            .Setup(r => r.GetAttributeValuesRangeAsync(mvo.Id, "Other Mobiles",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(new RangeResultSet<MetaverseObjectAttributeValue> { Results = values.ToList(), TotalResults = 11 });

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<MvoMvaTable>(), Is.True);
            Assert.That(cut.FindAll(".jim-attr-expanded-item"), Is.Empty);
        }
    }

    [Test]
    public void DetailsTable_ProvenanceLoadingWithNoDataYet_ShowsAProgressIndicatorNotAnEmptyBar()
    {
        var mvo = BuildObject(TextValue(1, "Job Title", "Engineer"));

        var cut = Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, mvo)
            .Add(c => c.ObjectTypeName, "User")
            .Add(c => c.ProvenanceLoading, true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<MudBlazor.MudProgressCircular>(), Is.True);
            Assert.That(cut.HasComponent<MvoContributionBar>(), Is.False);
        }
    }
}
