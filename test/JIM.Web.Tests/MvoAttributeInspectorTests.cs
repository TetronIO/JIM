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
    public void Inspector_ReferenceValue_LinksTheReferencedObjectUnderItsTypesPluralName()
    {
        // The link used to be built from the type's singular name (/t/Person/v/{id}), a page that does not exist;
        // the route takes the plural (#2151).
        var managerId = Guid.NewGuid();
        var provenance = BuildProvenance();
        provenance.CurrentValues =
        [
            new ProvenanceValue
            {
                DisplayValue = "Priya Shah",
                ReferenceMetaverseObjectId = managerId,
                ReferenceTypeName = "Person",
                ReferenceTypePluralName = "People"
            }
        ];

        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, provenance));

        Assert.That(cut.FindComponent<ObjectChip>().Instance.Href, Is.EqualTo($"/t/people/v/{managerId}"));
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
            // The Connected System and Synchronisation Rule share one "Source" row and one pill, the panel's only
            // pill-weight statement of the source; the list of every source names them quietly.
            var facts = cut.Find(".jim-inspector-facts");
            Assert.That(facts.QuerySelectorAll("dt").Select(d => d.TextContent), Does.Contain("Source"));
            Assert.That(facts.QuerySelectorAll("dt").Select(d => d.TextContent), Does.Not.Contain("Connected System"));
            var pill = cut.FindComponent<SystemRuleChip>().Instance;
            Assert.That(pill.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(pill.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(cut.FindComponents<SystemRuleChip>(), Has.Count.EqualTo(1), "every-source rows use dots, not pills");
            Assert.That(cut.FindComponents<ObjectChip>().Select(c => c.Instance.Kind), Does.Contain(ObjectChipKind.ConnectedSystemObject));
            Assert.That(cut.Markup, Does.Contain("Priya Shah"));
            Assert.That(cut.Markup, Does.Contain("Last set"));
            Assert.That(cut.FindAll($"a[href='/activity/item/{provenance.LastSet!.ActivityRunProfileExecutionItemId}']"),
                Is.Not.Empty);
        }
    }

    /// <summary>
    /// #2472: a value read from a sign-in token states that source, and the claim, in the Source row; its history
    /// entry names the claim where a synchronised value's names its rule.
    /// </summary>
    [Test]
    public void Inspector_SignInClaimOrigin_StatesTheClaimInTheSourceRowAndTheHistory()
    {
        var history = new List<AttributeHistoryEntry>
        {
            new()
            {
                Kind = AttributeHistoryChangeKind.Added,
                Value = "Engineer",
                ContributedByKind = ValueContributorKind.SignInClaim,
                ClaimType = "job_title",
                Change = new ProvenanceChange { ChangeTime = DateTime.UtcNow }
            }
        };
        var provenance = BuildProvenance(history: history);
        provenance.CurrentValues[0].Origin = new ValueOrigin { Kind = ValueOriginKind.SignInClaim, ClaimType = "job_title" };
        provenance.ContributingConnectedSystemObject = null;
        provenance.LastSet = null;

        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, provenance));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("Source not recorded"));
            Assert.That(cut.FindComponent<ValueOriginChip>().Instance.Origin.ClaimType, Is.EqualTo("job_title"));
            Assert.That(cut.Find(".jim-inspector-meta").TextContent, Does.Contain("job_title"));
        }
    }

    [Test]
    public void Inspector_SystemOrigin_StatesJimAsTheSource()
    {
        var provenance = BuildProvenance();
        provenance.CurrentValues[0].Origin = new ValueOrigin { Kind = ValueOriginKind.System };
        provenance.ContributingConnectedSystemObject = null;
        provenance.LastSet = null;

        var cut = Render<MvoAttributeInspector>(p => p.Add(c => c.Provenance, provenance));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("Source not recorded"));
            Assert.That(cut.FindComponent<ValueOriginChip>().Instance.Origin.Kind, Is.EqualTo(ValueOriginKind.System));
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
            Assert.That(cut.Markup, Does.Contain("Every source"));
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
            Assert.That(cut.Markup, Does.Contain(">History<"));
            Assert.That(cut.Markup, Does.Contain("Senior Engineer"));
            Assert.That(cut.Markup, Does.Contain("Engineer"));
            Assert.That(cut.Markup, Does.Contain("jim-inspector-previous"));
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
