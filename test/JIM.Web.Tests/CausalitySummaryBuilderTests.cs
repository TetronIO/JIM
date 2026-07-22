// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="CausalitySummaryBuilder"/>: the plain-English summary sentence (as segments,
/// never pre-rendered HTML) and the outcome pill strip, over the three PRD scenarios plus the
/// no-change, legacy-data and generic-fallback shapes.
/// </summary>
[TestFixture]
public class CausalitySummaryBuilderTests
{
    private static string RenderSentence(IEnumerable<SummarySegment> segments) =>
        string.Concat(segments.Select(s => s switch
        {
            SummarySegment.Text text => text.Value,
            SummarySegment.Entity entity => entity.Label,
            _ => string.Empty
        }));

    private static CausalitySummary BuildSummary(ActivityRunProfileExecutionItem item, CausalityPageContext context) =>
        CausalitySummaryBuilder.Build(CausalityModelBuilder.Build(item, context));

    [Test]
    public void Build_NewJoinerScenario_ProducesTheMockUpSentence()
    {
        var summary = BuildSummary(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC processed the record for Liam Allen (S8-287551): " +
            "a new Identity was created, 11 attributes flowed to it, and an export of 11 changes is now queued for Glitterband EMEA."));
    }

    [Test]
    public void Build_NewJoinerScenario_HighlightsEntitiesAsSegmentsWithHrefs()
    {
        var summary = BuildSummary(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var entities = summary.Segments.OfType<SummarySegment.Entity>().ToList();

        Assert.That(entities.Select(e => e.Label), Is.EqualTo(new[]
        {
            "Full Synchronisation", "Yellowstone APAC", "Liam Allen (S8-287551)", "Glitterband EMEA"
        }));

        var sourceSystem = entities[1];
        Assert.That(sourceSystem.Kind, Is.EqualTo(CausalityEntityKind.ConnectedSystem));
        Assert.That(sourceSystem.Href, Is.EqualTo("/admin/connected-systems/1"));

        var record = entities[2];
        Assert.That(record.Kind, Is.EqualTo(CausalityEntityKind.Record));
        Assert.That(record.Href, Is.EqualTo($"/admin/connected-systems/1/connector-space/{CausalityTestData.CsoId}"));

        var downstreamSystem = entities[3];
        Assert.That(downstreamSystem.Kind, Is.EqualTo(CausalityEntityKind.ConnectedSystem));
        Assert.That(downstreamSystem.Href, Is.EqualTo("/admin/connected-systems/2"));
    }

    [Test]
    public void Build_NewJoinerScenario_ProducesTheMockUpPills()
    {
        var summary = BuildSummary(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(summary.Pills.Select(p => (p.Label, p.Tone)), Is.EqualTo(new[]
        {
            ("Identity created", CausalityTone.Primary),
            ("11 attributes flowed", CausalityTone.Secondary),
            ("Provisioned · 1 system", CausalityTone.Primary),
            ("Export queued · 11 changes", CausalityTone.Info)
        }));
    }

    [Test]
    public void Build_LeaverScenario_NamesTheRuleTheDeletedIdentityAndTheDeprovisionedSystems()
    {
        var context = new CausalityPageContext(
            ConnectedSystemId: 1,
            ConnectedSystemName: "Yellowstone APAC",
            RunProfileName: "Full Synchronisation",
            CsoId: CausalityTestData.CsoId,
            CsoDisplayName: "Erin Byrne",
            CsoExternalId: "S8-100",
            CsoObjectTypeName: "person",
            MvoTypeName: "Person",
            MvoTypePluralName: "People");

        var summary = BuildSummary(CausalityTestData.LeaverItem(), context);

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC processed the record for Erin Byrne (S8-100): " +
            "it left the scope of Synchronisation Rule Yellowstone People - Inbound, the Identity Erin Byrne was deleted, " +
            "and deprovisioning is now queued for 2 systems."));
    }

    [Test]
    public void Build_LeaverScenario_LinksTheRuleAndTheDeletionRecord()
    {
        var summary = BuildSummary(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var entities = summary.Segments.OfType<SummarySegment.Entity>().ToList();

        var rule = entities.SingleOrDefault(e => e.Kind == CausalityEntityKind.SynchronisationRule);
        Assert.That(rule, Is.Not.Null);
        Assert.That(rule!.Label, Is.EqualTo("Yellowstone People - Inbound"));
        Assert.That(rule.Href, Is.EqualTo("/admin/sync-rules/7"));

        var deletedIdentity = entities.SingleOrDefault(e => e.Kind == CausalityEntityKind.Identity);
        Assert.That(deletedIdentity, Is.Not.Null);
        Assert.That(deletedIdentity!.Label, Is.EqualTo("Erin Byrne"));
        Assert.That(deletedIdentity.Href, Is.EqualTo("/admin/deleted-objects"),
            "A deleted Identity links to the durable deletion record browser, not its (gone) detail page");
    }

    [Test]
    public void Build_LeaverScenario_ProducesOutOfScopeAndIdentityDeletedPills()
    {
        var summary = BuildSummary(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var labels = summary.Pills.Select(p => p.Label).ToList();

        Assert.That(labels, Does.Contain("Out of scope"));
        Assert.That(labels, Does.Contain("Identity deleted"));

        var identityDeleted = summary.Pills.Single(p => p.Label == "Identity deleted");
        Assert.That(identityDeleted.Tone, Is.EqualTo(CausalityTone.Error));
    }

    [Test]
    public void Build_ExportFailureScenario_ProducesAttemptedAndFailedSentence()
    {
        var summary = BuildSummary(CausalityTestData.ExportFailureItem(), CausalityTestData.ExportContext());

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "An Export on Glitterband EMEA processed the record for Liam Allen (S8-287551): " +
            "an export of 3 changes was attempted, but it failed and needs attention."));
    }

    [Test]
    public void Build_ExportFailureScenario_ProducesExportedAndFailedPills()
    {
        var summary = BuildSummary(CausalityTestData.ExportFailureItem(), CausalityTestData.ExportContext());

        Assert.That(summary.Pills.Select(p => (p.Label, p.Tone)), Is.EqualTo(new[]
        {
            ("Exported · 3 changes", CausalityTone.Info),
            ("Export failed", CausalityTone.Error)
        }));
    }

    [Test]
    public void Build_NoOutcomes_ProducesNoChangeSentenceAndNoPills()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };

        var summary = BuildSummary(item, CausalityTestData.NewJoinerContext());

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC processed the record for Liam Allen (S8-287551): no changes were needed."));
        Assert.That(summary.Pills, Is.Empty);
    }

    [Test]
    public void Build_UnanticipatedShape_ProducesGenericFallbackSentence()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, parent: null, ordinal: 0);

        var summary = BuildSummary(item, CausalityTestData.NewJoinerContext());

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC processed the record for Liam Allen (S8-287551): Drift corrected."));
        Assert.That(summary.Pills.Select(p => (p.Label, p.Tone)), Is.EqualTo(new[]
        {
            ("Drift corrected", CausalityTone.Warning)
        }));
    }

    [Test]
    public void Build_JoinShape_NamesAndLinksTheJoinedIdentity()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var joined = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
            parent: null, ordinal: 0, targetEntityId: CausalityTestData.MvoId, targetEntityDescription: "Liam Allen");
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            parent: joined, ordinal: 0, detailCount: 5);

        var summary = BuildSummary(item, CausalityTestData.NewJoinerContext());

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC processed the record for Liam Allen (S8-287551): " +
            "it was joined to the Identity Liam Allen, and 5 attributes flowed to it."));

        var identity = summary.Segments.OfType<SummarySegment.Entity>().Single(e => e.Kind == CausalityEntityKind.Identity);
        Assert.That(identity.Href, Is.EqualTo($"/t/people/v/{CausalityTestData.MvoId}"));
    }

    [Test]
    public void Build_LegacyLeaverWithoutAttribution_FallsBackToUnnamedRuleAndIdentity()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var outOfScope = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            parent: null, ordinal: 0);
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
            parent: outOfScope, ordinal: 1);

        var summary = BuildSummary(item, CausalityTestData.NewJoinerContext());
        var sentence = RenderSentence(summary.Segments);

        Assert.That(sentence, Does.Contain("it left the scope of its Synchronisation Rule"));
        Assert.That(sentence, Does.Contain("the Identity was deleted"));
        Assert.That(sentence, Does.EndWith("."));
    }

    [Test]
    public void Build_EmptyContextAndNoOutcomes_StillProducesAValidSentence()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };

        var summary = BuildSummary(item, CausalityTestData.EmptyContext());
        var sentence = RenderSentence(summary.Segments);

        Assert.That(sentence, Is.Not.Empty);
        Assert.That(sentence, Does.EndWith("."));
    }

    [Test]
    public void Build_AnyScenario_NeverEmitsEmDashes()
    {
        var scenarios = new[]
        {
            BuildSummary(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext()),
            BuildSummary(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext()),
            BuildSummary(CausalityTestData.ExportFailureItem(), CausalityTestData.ExportContext())
        };

        foreach (var summary in scenarios)
        {
            Assert.That(RenderSentence(summary.Segments), Does.Not.Contain('—'));
            foreach (var pill in summary.Pills)
                Assert.That(pill.Label, Does.Not.Contain('—'));
        }
    }
}
