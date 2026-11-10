// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// <see cref="SynchronisationHistoryBatch.ReleaseRunObjects"/> (#2874): a batch written while the run goes on keeps every
/// id it is written by and no reference into the objects the run is still working on.
/// </summary>
[TestFixture]
public class SynchronisationHistoryBatchReleaseTests
{
    [Test]
    public void ReleaseRunObjects_PreparedBatch_CutsTheRunsObjectsAndKeepsEveryId()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var cause = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var causeOutcome = new ActivityRunProfileExecutionItemSyncOutcome { Id = Guid.NewGuid() };
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = Guid.NewGuid(), ConnectedSystemObject = cso };
        var effectOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(), ActivityRunProfileExecutionItem = item, ActivityRunProfileExecutionItemId = item.Id
        };
        var edge = new CausalEdge
        {
            Id = Guid.NewGuid(), EffectRunProfileExecutionItem = item, EffectRunProfileExecutionItemId = item.Id,
            EffectSyncOutcome = effectOutcome, EffectSyncOutcomeId = effectOutcome.Id,
            CauseRunProfileExecutionItem = cause, CauseRunProfileExecutionItemId = cause.Id,
            CauseSyncOutcome = causeOutcome, CauseSyncOutcomeId = causeOutcome.Id
        };
        var itemChange = new ConnectedSystemObjectChange { Id = Guid.NewGuid(), ActivityRunProfileExecutionItem = item, ActivityRunProfileExecutionItemId = item.Id };
        var mvoChange = new MetaverseObjectChange { Id = Guid.NewGuid(), ActivityRunProfileExecutionItem = item, ActivityRunProfileExecutionItemId = item.Id };
        var batch = new SynchronisationHistoryBatch
        {
            Items = [item],
            ItemIdsWrittenWithDeletion = new HashSet<Guid>(),
            Outcomes = [effectOutcome],
            OutcomeChangeRecords = [],
            ItemChangeRecords = [itemChange],
            CausalEdges = [edge],
            MatchCandidates = [],
            MetaverseObjectChanges = [mvoChange],
            MetaverseObjectChangeAppends = [],
            CounterDeltas = new Dictionary<ActivityStatCounterKey, long>(),
            Page = 1,
            TotalPages = 1
        };

        batch.ReleaseRunObjects();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.ConnectedSystemObject, Is.Null);
            Assert.That(item.ConnectedSystemObjectId, Is.EqualTo(cso.Id), "the item still names its object, by id");
            Assert.That(edge.CauseRunProfileExecutionItem, Is.Null);
            Assert.That(edge.CauseSyncOutcome, Is.Null);
            Assert.That(edge.EffectSyncOutcome, Is.Null);
            Assert.That(edge.EffectRunProfileExecutionItem, Is.Null);
            Assert.That((edge.EffectRunProfileExecutionItemId, edge.EffectSyncOutcomeId, edge.CauseRunProfileExecutionItemId, edge.CauseSyncOutcomeId),
                Is.EqualTo((item.Id, (Guid?)effectOutcome.Id, (Guid?)cause.Id, (Guid?)causeOutcome.Id)));
            Assert.That(itemChange.ActivityRunProfileExecutionItem, Is.Null);
            Assert.That(itemChange.ActivityRunProfileExecutionItemId, Is.EqualTo(item.Id));
            Assert.That(mvoChange.ActivityRunProfileExecutionItem, Is.Null);
            Assert.That(mvoChange.ActivityRunProfileExecutionItemId, Is.EqualTo(item.Id));
            Assert.That(() => batch.RequireEveryIdAssigned("test"), Throws.Nothing);
        }
    }
}
