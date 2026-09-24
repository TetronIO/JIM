// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// <see cref="ActivityTargetOperationType"/> is persisted by ordinal on <see cref="Activity.TargetOperationType"/>,
/// and those rows are the audit record of what an administrator (or JIM itself) did. Inserting a value in the
/// middle, or reordering two, silently re-labels every historical Activity already in a customer's database: a
/// Create would read back as a Delete, and nothing would fail. The enum is therefore append-only, and this pins
/// every existing ordinal so a reorder fails here rather than misinforming an administrator reading the Activity
/// list.
///
/// Adding a value is expected; add it to the end, and add its ordinal to the map below in the same change.
/// </summary>
[TestFixture]
public class ActivityTargetOperationTypeOrdinalTests
{
    /// <summary>
    /// Every value and the ordinal it is stored as. Do not edit an existing entry; append.
    /// </summary>
    private static readonly Dictionary<ActivityTargetOperationType, int> ExpectedOrdinals = new()
    {
        [ActivityTargetOperationType.Create] = 0,
        [ActivityTargetOperationType.Read] = 1,
        [ActivityTargetOperationType.Update] = 2,
        [ActivityTargetOperationType.Delete] = 3,
        [ActivityTargetOperationType.Clear] = 4,
        [ActivityTargetOperationType.Execute] = 5,
        [ActivityTargetOperationType.ImportHierarchy] = 6,
        [ActivityTargetOperationType.ImportSchema] = 7,
        [ActivityTargetOperationType.Revert] = 8,
        [ActivityTargetOperationType.Reset] = 9,
        [ActivityTargetOperationType.Authenticate] = 10,
        [ActivityTargetOperationType.Preview] = 11,
        [ActivityTargetOperationType.SetPassword] = 12,
        [ActivityTargetOperationType.SchemaRefreshRemoval] = 13,
        [ActivityTargetOperationType.RetryPasswordDelivery] = 14,
        [ActivityTargetOperationType.CancelPasswordDelivery] = 15,
        [ActivityTargetOperationType.DiscoverAuxiliaryClasses] = 16,
        [ActivityTargetOperationType.RecallAttributeValues] = 17,
        [ActivityTargetOperationType.Deprovision] = 18,

        // Unique Value Generation (#242, Phase 3): "Start again" moving a generated Sequence mapping's counter.
        // Distinct from Reset (9), which is reserved for a system-wide factory reset.
        [ActivityTargetOperationType.RestartGeneratedValues] = 19
    };

    [Test]
    public void ActivityTargetOperationType_EveryValue_KeepsItsPersistedOrdinal()
    {
        var drifted = ExpectedOrdinals
            .Where(pair => (int)pair.Key != pair.Value)
            .Select(pair => $"{pair.Key} is {(int)pair.Key}, expected {pair.Value}")
            .ToList();

        Assert.That(drifted, Is.Empty,
            "an operation type's ordinal changed, which silently re-labels every Activity row already persisted: " +
            string.Join("; ", drifted));
    }

    [Test]
    public void ActivityTargetOperationType_EveryDeclaredValue_IsPinned()
    {
        var unpinned = Enum.GetValues<ActivityTargetOperationType>()
            .Where(value => !ExpectedOrdinals.ContainsKey(value))
            .ToList();

        Assert.That(unpinned, Is.Empty,
            "new operation type(s) not pinned in this test: " + string.Join(", ", unpinned) +
            ". Append them here with the ordinal they were assigned, so a later reorder is caught.");
    }
}
