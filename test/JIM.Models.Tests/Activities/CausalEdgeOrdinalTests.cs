// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// <see cref="CausalEdgeType"/> and <see cref="CausalReasonCode"/> are persisted by ordinal on causal edge rows
/// (#1223), and those rows are the record of why a change happened. Inserting a value in the middle, or reordering
/// two, silently re-labels every edge already stored: a cascade caused by an authoritative source disconnecting
/// would read back as one caused by scope loss, cohorts would group under the wrong statement, and nothing would
/// fail. Both enums are therefore append-only, and this pins every existing ordinal so a reorder fails here rather
/// than misinforming an administrator investigating a live cascade.
///
/// Adding a value is expected; add it to the end, and add its ordinal to the relevant map below in the same change.
/// </summary>
[TestFixture]
public class CausalEdgeOrdinalTests
{
    /// <summary>
    /// Every cascade seam and the ordinal it is stored as. Do not edit an existing entry; append.
    /// </summary>
    private static readonly Dictionary<CausalEdgeType, int> ExpectedEdgeTypeOrdinals = new()
    {
        [CausalEdgeType.MetaverseObjectDeletionCausedDeprovision] = 0,
        [CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval] = 1,
        [CausalEdgeType.ExportCausedImportConfirmation] = 2,
        [CausalEdgeType.PendingExportQueueingCausedExportExecution] = 3
    };

    /// <summary>
    /// Every reason code and the ordinal it is stored as. Do not edit an existing entry; append.
    /// </summary>
    private static readonly Dictionary<CausalReasonCode, int> ExpectedReasonCodeOrdinals = new()
    {
        [CausalReasonCode.NotSet] = 0,
        [CausalReasonCode.LastConnectorDisconnected] = 1,
        [CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured] = 2,
        [CausalReasonCode.AllAuthoritativeSourcesDisconnected] = 3,
        [CausalReasonCode.AuthoritativeSourceDisconnected] = 4,
        [CausalReasonCode.ExportCreateStaged] = 5,
        [CausalReasonCode.ExportUpdateStaged] = 6,
        [CausalReasonCode.ExportDeleteStaged] = 7
    };

    [Test]
    public void CausalEdgeType_EveryValue_KeepsItsPersistedOrdinal()
    {
        var drifted = ExpectedEdgeTypeOrdinals
            .Where(pair => (int)pair.Key != pair.Value)
            .Select(pair => $"{pair.Key} is {(int)pair.Key}, expected {pair.Value}")
            .ToList();

        Assert.That(drifted, Is.Empty,
            "a causal edge type's ordinal changed, which silently re-labels every causal edge already persisted: " +
            string.Join("; ", drifted));
    }

    [Test]
    public void CausalEdgeType_EveryDeclaredValue_IsPinned()
    {
        var unpinned = Enum.GetValues<CausalEdgeType>()
            .Where(value => !ExpectedEdgeTypeOrdinals.ContainsKey(value))
            .ToList();

        Assert.That(unpinned, Is.Empty,
            "new causal edge type(s) not pinned in this test: " + string.Join(", ", unpinned) +
            ". Append them here with the ordinal they were assigned, so a later reorder is caught.");
    }

    [Test]
    public void CausalReasonCode_EveryValue_KeepsItsPersistedOrdinal()
    {
        var drifted = ExpectedReasonCodeOrdinals
            .Where(pair => (int)pair.Key != pair.Value)
            .Select(pair => $"{pair.Key} is {(int)pair.Key}, expected {pair.Value}")
            .ToList();

        Assert.That(drifted, Is.Empty,
            "a causal reason code's ordinal changed, which silently re-labels every causal edge already persisted: " +
            string.Join("; ", drifted));
    }

    [Test]
    public void CausalReasonCode_EveryDeclaredValue_IsPinned()
    {
        var unpinned = Enum.GetValues<CausalReasonCode>()
            .Where(value => !ExpectedReasonCodeOrdinals.ContainsKey(value))
            .ToList();

        Assert.That(unpinned, Is.Empty,
            "new causal reason code(s) not pinned in this test: " + string.Join(", ", unpinned) +
            ". Append them here with the ordinal they were assigned, so a later reorder is caught.");
    }
}
