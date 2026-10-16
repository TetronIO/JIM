// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging.DTOs;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What the causality panel is given for an execution item (#2100), read by <see cref="CausalityItemLoader"/> for the
/// item page and the Causality Explorer alike.
/// </summary>
/// <remarks>
/// <para>
/// The page context's two Connected System identities must not be confused.
/// <see cref="CausalityPageContext.ConnectedSystemId"/>/<see cref="CausalityPageContext.ConnectedSystemName"/> describe
/// the Connected System the Run Profile executed against (the Activity's system), while
/// <see cref="CausalityPageContext.CsoConnectedSystemId"/>/<see cref="CausalityPageContext.CsoConnectedSystemName"/>
/// independently describe the item's Connected System Object's own system. These normally coincide, but diverge for
/// cross-system cascades, e.g. a Full Sync on system A provisioning or exporting to a Connected System Object on system
/// B: the run and record identities must each stay correct for their own consumers (see
/// <see cref="CausalityPageContext"/> for the full list).
/// </para>
/// <para>
/// These tests began on the item page, most of them reaching its private state by reflection because the page had no
/// render harness; the logic they pin moved here with the loader, unchanged, and so did they.
/// </para>
/// </remarks>
[TestFixture]
public class CausalityItemLoaderTests
{
    [Test]
    public void ShowsCausality_ExportHeldForADecision_ShowsTheBandThoughNothingWasRecorded()
    {
        // A held export (#242, release 4) records no outcome, since nothing was exported; its band is what says why.
        var held = new ActivityRunProfileExecutionItem { ErrorType = ActivityRunProfileExecutionItemErrorType.GeneratedValueCollisionUnresolved };
        var failed = new ActivityRunProfileExecutionItem { ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CausalityItemLoader.ShowsCausality(held), Is.True);
            Assert.That(CausalityItemLoader.ShowsCausality(failed), Is.False, "an item with nothing recorded and an ordinary error has no story to tell");
        }
    }

    [Test]
    public void BuildContext_RunSystemDiffersFromObjectSystem_UsesRunSystem()
    {
        // Arrange: a cross-system cascade, e.g. a Full Sync on Yellowstone APAC (the run's system)
        // producing an execution item for a Connected System Object that lives on Glitterband EMEA.
        var runSystem = new ConnectedSystemHeader { Id = 1, Name = "Yellowstone APAC" };
        var objectSystem = new ConnectedSystemHeader { Id = 2, Name = "Glitterband EMEA" };
        var data = new CausalityItemData
        {
            Item = new ActivityRunProfileExecutionItem(),
            RunConnectedSystem = runSystem,
            RecordConnectedSystem = objectSystem
        };

        // Act
        var context = CausalityItemLoader.BuildContext(data);

        // Assert: ConnectedSystemId/Name name the run's system, not the object's own system...
        Assert.That(context.ConnectedSystemId, Is.EqualTo(runSystem.Id));
        Assert.That(context.ConnectedSystemName, Is.EqualTo(runSystem.Name));
        // ...while CsoConnectedSystemId/Name independently carry the object's own system, so
        // record-scoped consumers (the record hyperlink, per-event system badges) still get it right.
        Assert.That(context.CsoConnectedSystemId, Is.EqualTo(objectSystem.Id));
        Assert.That(context.CsoConnectedSystemName, Is.EqualTo(objectSystem.Name));
    }

    [Test]
    public void BuildContext_RunSystemSameAsObjectSystem_UsesSharedSystem()
    {
        // Arrange: the common case, where the run executed directly against the object's own system.
        var sharedSystem = new ConnectedSystemHeader { Id = 1, Name = "Yellowstone APAC" };
        var data = new CausalityItemData
        {
            Item = new ActivityRunProfileExecutionItem(),
            RunConnectedSystem = sharedSystem,
            RecordConnectedSystem = sharedSystem
        };

        // Act
        var context = CausalityItemLoader.BuildContext(data);

        // Assert
        Assert.That(context.ConnectedSystemId, Is.EqualTo(sharedSystem.Id));
        Assert.That(context.ConnectedSystemName, Is.EqualTo(sharedSystem.Name));
        Assert.That(context.CsoConnectedSystemId, Is.EqualTo(sharedSystem.Id));
        Assert.That(context.CsoConnectedSystemName, Is.EqualTo(sharedSystem.Name));
    }

    [Test]
    public void BuildContext_RunSystemUnresolved_DegradesToNullRatherThanObjectSystem()
    {
        // Arrange: the run's Connected System could not be resolved (e.g. an Activity with no
        // ConnectedSystemId). The panel must degrade to null rather than silently falling back to
        // the object's own system, per CausalityPageContext's documented "degrade gracefully" contract.
        var objectSystem = new ConnectedSystemHeader { Id = 2, Name = "Glitterband EMEA" };
        var data = new CausalityItemData
        {
            Item = new ActivityRunProfileExecutionItem(),
            // The run's system deliberately left unset (null).
            RecordConnectedSystem = objectSystem
        };

        // Act
        var context = CausalityItemLoader.BuildContext(data);

        // Assert: the run identity degrades to null...
        Assert.That(context.ConnectedSystemId, Is.Null);
        Assert.That(context.ConnectedSystemName, Is.Null);
        // ...independently of the object identity, which is unaffected and still resolves.
        Assert.That(context.CsoConnectedSystemId, Is.EqualTo(objectSystem.Id));
        Assert.That(context.CsoConnectedSystemName, Is.EqualTo(objectSystem.Name));
    }

    [Test]
    public void BuildContext_ExportScopeReviewItem_NamesTheMetaverseObjectAndNoRecord()
    {
        // The item has no Connected System Object. Its name snapshot is the Metaverse Object's, and the record system the
        // page resolves for it is the run's; passing either as a record made the panel invent a source object (#1982).
        var metaverseObjectId = Guid.NewGuid();
        var data = new CausalityItemData
        {
            Item = new ActivityRunProfileExecutionItem
            {
                ObjectChangeType = ObjectChangeType.ExportScopeReview,
                MetaverseObjectId = metaverseObjectId,
                DisplayNameSnapshot = "Carol Ng",
                ObjectTypeSnapshot = "Person"
            },
            RunConnectedSystem = new ConnectedSystemHeader { Id = 1, Name = "HR" },
            RecordConnectedSystem = new ConnectedSystemHeader { Id = 1, Name = "HR" },
            IdentityHeader = new JIM.Models.Core.DTOs.MetaverseObjectHeader
            {
                Id = metaverseObjectId, TypeName = "Person", TypePluralName = "People", CachedDisplayName = "Carol Ng"
            }
        };

        var context = CausalityItemLoader.BuildContext(data);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.SubjectMetaverseObjectId, Is.EqualTo(metaverseObjectId));
            Assert.That(context.SubjectMetaverseObjectName, Is.EqualTo("Carol Ng"));
            Assert.That(context.CsoDisplayName, Is.Null);
            Assert.That(context.CsoConnectedSystemId, Is.Null);
            Assert.That(context.CsoObjectTypeName, Is.Null);
        }
    }

    /// <summary>
    /// The context carries the host's link policy (#2100): the explorer's, where it read the item, so every link the panel
    /// writes re-centres the explorer; the item page's own otherwise.
    /// </summary>
    [Test]
    public void BuildContext_CarriesTheHostsLinkPolicy()
    {
        var item = new ActivityRunProfileExecutionItem();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new CausalityItemData { Item = item, Links = TestCausalityLinks.Instance }.Context.Links,
                Is.SameAs(TestCausalityLinks.Instance));
            Assert.That(new CausalityItemData { Item = item }.Context.Links, Is.SameAs(CausalityPageLinks.Instance));
        }
    }

    [Test]
    public void ResolveIdentityId_ExportScopeReviewItem_IsTheMetaverseObjectTheItemNames()
    {
        // A review item has no Connected System Object and no outcome targeting its Metaverse Object (its outcomes
        // target the accounts the review provisioned or deprovisioned), so the id it carries is the only way to find
        // the object it is about (#1971).
        var metaverseObjectId = Guid.NewGuid();
        var item = new ActivityRunProfileExecutionItem
        {
            ObjectChangeType = ObjectChangeType.ExportScopeReview,
            MetaverseObjectId = metaverseObjectId,
            SyncOutcomes =
            [
                new ActivityRunProfileExecutionItemSyncOutcome
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected,
                    TargetEntityId = Guid.NewGuid()
                }
            ]
        };

        Assert.That(CausalityItemLoader.ResolveIdentityId(item), Is.EqualTo(metaverseObjectId));
    }

    [Test]
    public void ResolveIdentityId_ItemWithoutAMetaverseObjectId_IsTheObjectItsIdentityOutcomeTargets()
    {
        var projectedId = Guid.NewGuid();
        var item = new ActivityRunProfileExecutionItem
        {
            ObjectChangeType = ObjectChangeType.Projected,
            SyncOutcomes =
            [
                new ActivityRunProfileExecutionItemSyncOutcome
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                    TargetEntityId = projectedId
                }
            ]
        };

        Assert.That(CausalityItemLoader.ResolveIdentityId(item), Is.EqualTo(projectedId));
    }
}
