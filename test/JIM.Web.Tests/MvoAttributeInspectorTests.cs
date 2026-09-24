// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Inspect view's attribute inspector (#399): the current value and origin sentence, every source in
/// priority order with its state, the history rail, and the sections that hide themselves when there is nothing
/// to show (no sources recorded).
/// </summary>
[TestFixture]
public class MvoAttributeInspectorTests : JimComponentTestContext
{
    private static MetaverseAttributeProvenance BuildProvenance(
        List<AttributeSourceCandidate>? sources = null,
        List<AttributeHistoryEntry>? history = null)
    {
        return new MetaverseAttributeProvenance
        {
            MetaverseObjectId = Guid.NewGuid(),
            MetaverseObjectTypeId = 3,
            AttributeId = 42,
            AttributeName = "Job Title",
            AttributeType = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            CurrentValues =
            [
                new ProvenanceValue
                {
                    DisplayValue = "Engineer",
                    Origin = new ValueOrigin
                    {
                        Kind = ValueOriginKind.SynchronisationRule,
                        ConnectedSystemId = 1,
                        ConnectedSystemName = "HR",
                        SyncRuleId = 5,
                        SyncRuleName = "HR Import"
                    }
                }
            ],
            CurrentValueTotalCount = 1,
            ContributingConnectedSystemObject = new ProvenanceConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = 1,
                ConnectedSystemName = "HR",
                TypeName = "person",
                DisplayName = "Priya Shah",
                ExternalId = "EMP1"
            },
            LastSet = new ProvenanceChange
            {
                ChangeTime = DateTime.UtcNow.AddDays(-2),
                ActivityRunProfileExecutionItemId = Guid.NewGuid(),
                ActivityDescription = "Delta Synchronisation"
            },
            Sources = sources ?? [],
            History = history ?? []
        };
    }

    [Test]
    public void Inspector_RendersHeaderAndCurrentValueAndOriginSentence()
    {
        var provenance = BuildProvenance();
        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, provenance));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Job Title"));
            Assert.That(cut.Markup, Does.Contain("Text"));
            Assert.That(cut.Markup, Does.Contain("Single"));
            Assert.That(cut.Markup, Does.Contain("Engineer"));
            Assert.That(cut.Markup, Does.Contain("Set by"));
            Assert.That(cut.HasComponent<ValueOriginChip>(), Is.True);
            Assert.That(cut.Markup, Does.Contain("Priya Shah"));
            Assert.That(cut.FindAll($"a[href='/activity/item/{provenance.LastSet!.ActivityRunProfileExecutionItemId}']"),
                Is.Not.Empty);
        }
    }

    [Test]
    public void Inspector_NotRecordedOrigin_RendersSourceNotRecordedInsteadOfTheSentence()
    {
        var provenance = BuildProvenance();
        provenance.CurrentValues[0].Origin = ValueOrigin.NotRecorded;

        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, provenance));

        Assert.That(cut.Markup, Does.Contain("Source not recorded"));
    }

    [Test]
    public void Inspector_CloseButtonClicked_RaisesOnClose()
    {
        var closed = false;
        var cut = Render<MvoAttributeInspector>(p => p
            .Add(c => c.Provenance, BuildProvenance())
            .Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        cut.Find("button").Click();

        Assert.That(closed, Is.True);
    }

    [Test]
    public void Inspector_NoSources_HidesTheSourcesSection()
    {
        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, BuildProvenance()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("Every source for this attribute"));
            Assert.That(cut.Markup, Does.Not.Contain("Change priority"));
        }
    }

    [TestCase(AttributeSourceState.InUse, "In use")]
    [TestCase(AttributeSourceState.Outranked, "Outranked")]
    [TestCase(AttributeSourceState.NoValue, "No value")]
    [TestCase(AttributeSourceState.NotJoined, "Not joined")]
    [TestCase(AttributeSourceState.Disabled, "Disabled")]
    [TestCase(AttributeSourceState.NotEvaluated, "Not evaluated")]
    public void Inspector_EverySourceState_RendersItsLabelAndTheNoteAsATooltip(AttributeSourceState state, string label)
    {
        var sources = new List<AttributeSourceCandidate>
        {
            new()
            {
                Rank = 1,
                MappingId = 1,
                SyncRuleId = 5,
                SyncRuleName = "HR Import",
                ConnectedSystemId = 1,
                ConnectedSystemName = "HR",
                State = state,
                CandidateValues = ["Engineer"],
                Note = "some context"
            }
        };
        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, BuildProvenance(sources: sources)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Every source for this attribute"));
            Assert.That(cut.Markup, Does.Contain(label));
            // Earlier tooltips belong to the Close button and the relative-time display; with one source and no
            // history, the state chip's tooltip is the last one in the panel.
            Assert.That(cut.FindComponents<MudBlazor.MudTooltip>().Last().Instance.Text, Is.EqualTo("some context"));
            Assert.That(cut.Markup, Does.Contain("Change priority"));
        }
    }

    [Test]
    public void Inspector_HistoryEntries_RenderKindValueAndStruckThroughPreviousValueForSet()
    {
        var history = new List<AttributeHistoryEntry>
        {
            new()
            {
                Kind = AttributeHistoryChangeKind.Set,
                Value = "Senior Engineer",
                PreviousValue = "Engineer",
                Change = new ProvenanceChange { ChangeTime = DateTime.UtcNow.AddDays(-1) }
            }
        };
        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, BuildProvenance(history: history)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("History of this attribute"));
            Assert.That(cut.Markup, Does.Contain("Senior Engineer"));
            Assert.That(cut.Markup, Does.Contain("Engineer"));
            Assert.That(cut.Markup, Does.Contain("text-decoration: line-through"));
        }
    }

    [Test]
    public void Inspector_NoHistory_SaysSoRatherThanShowingAnEmptyRail()
    {
        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, BuildProvenance()));

        Assert.That(cut.Markup, Does.Contain("No changes recorded for this attribute."));
    }

    [Test]
    public void Inspector_HistoryTruncated_ShowsTheTruncationNote()
    {
        var provenance = BuildProvenance(history:
        [
            new AttributeHistoryEntry
            {
                Kind = AttributeHistoryChangeKind.Added,
                Value = "x",
                Change = new ProvenanceChange { ChangeTime = DateTime.UtcNow }
            }
        ]);
        provenance.HistoryTruncated = true;

        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, provenance));

        Assert.That(cut.Markup, Does.Contain("Showing the most recent changes only."));
    }

    [Test]
    public void Inspector_OpenInTimelineLinkClicked_RaisesOnOpenInTimeline()
    {
        var opened = false;
        var cut = Render<MvoAttributeInspector>(p => p
            .Add(c => c.Provenance, BuildProvenance())
            .Add(c => c.OnOpenInTimeline, EventCallback.Factory.Create(this, () => opened = true)));

        var link = cut.FindAll("a").First(a => a.TextContent.Contains("Open in Timeline"));
        link.Click();

        Assert.That(opened, Is.True);
    }
}
