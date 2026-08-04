// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// <see cref="ActivityRunProfileExecutionItemSyncOutcomeType"/> is persisted by ordinal, and its rows are the audit
/// record of what synchronisation did. Inserting a value in the middle, or reordering two, silently re-labels every
/// historical outcome already in a customer's database: a run that projected objects would read back as having
/// deleted them, and nothing would fail. The enum is therefore append-only, and this pins every existing ordinal so
/// that a reorder fails here rather than in production.
///
/// Adding a value is expected; add it to the end, and add its ordinal to the map below in the same change.
/// </summary>
[TestFixture]
public class SyncOutcomeTypeOrdinalTests
{
    /// <summary>
    /// Every value and the ordinal it is stored as. Do not edit an existing entry; append.
    /// </summary>
    private static readonly Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int> ExpectedOrdinals = new()
    {
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded] = 0,
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated] = 1,
        [ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted] = 2,
        [ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected] = 3,
        [ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed] = 4,
        [ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed] = 5,
        [ActivityRunProfileExecutionItemSyncOutcomeType.Projected] = 6,
        [ActivityRunProfileExecutionItemSyncOutcomeType.Joined] = 7,
        [ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow] = 8,
        [ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected] = 9,
        [ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope] = 10,
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted] = 11,
        [ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection] = 12,
        [ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned] = 13,
        [ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated] = 14,
        [ActivityRunProfileExecutionItemSyncOutcomeType.Exported] = 15,
        [ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned] = 16,
        [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled] = 17,
        [ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull] = 18,
        [ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor] = 19,

        // Configuration change preview (#827). These describe what *would* happen and are never written by a
        // synchronisation run; they exist here rather than in a parallel enum so a preview delta and a sync outcome
        // speak one vocabulary.
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope] = 20,
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope] = 21,
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible] = 22,
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible] = 23,
        [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate] = 24
    };

    [Test]
    public void SyncOutcomeType_EveryValue_KeepsItsPersistedOrdinal()
    {
        var drifted = ExpectedOrdinals
            .Where(pair => (int)pair.Key != pair.Value)
            .Select(pair => $"{pair.Key} is {(int)pair.Key}, expected {pair.Value}")
            .ToList();

        Assert.That(drifted, Is.Empty,
            "an outcome type's ordinal changed, which silently re-labels every outcome row already persisted: " +
            string.Join("; ", drifted));
    }

    [Test]
    public void SyncOutcomeType_EveryDeclaredValue_IsPinned()
    {
        var unpinned = Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>()
            .Where(value => !ExpectedOrdinals.ContainsKey(value))
            .ToList();

        Assert.That(unpinned, Is.Empty,
            "new outcome type(s) not pinned in this test: " + string.Join(", ", unpinned) +
            ". Append them here with the ordinal they were assigned, so a later reorder is caught.");
    }
}
