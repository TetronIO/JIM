// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// <see cref="PendingExportStatus"/> is persisted by ordinal on Pending Export rows, and those rows drive the
/// Connected System's retry and reporting behaviour. Inserting a value in the middle, or reordering two, silently
/// re-labels every Pending Export already in a customer's database. The enum is therefore append-only, and this
/// pins every existing ordinal so a reorder fails here rather than in production.
///
/// Adding a value is expected; add it to the end, and add its ordinal to the map below in the same change.
/// </summary>
[TestFixture]
public class PendingExportStatusOrdinalTests
{
    /// <summary>
    /// Every value and the ordinal it is stored as. Do not edit an existing entry; append.
    /// </summary>
    private static readonly Dictionary<PendingExportStatus, int> ExpectedOrdinals = new()
    {
        [PendingExportStatus.Pending] = 0,
        [PendingExportStatus.ExportNotConfirmed] = 1,
        [PendingExportStatus.Executing] = 2,
        [PendingExportStatus.Failed] = 3,
        [PendingExportStatus.Exported] = 4,

        // Unique Value Generation (#242, release 4): the export is held back for an administrator's decision on
        // its generated value.
        [PendingExportStatus.Parked] = 5
    };

    [Test]
    public void PendingExportStatus_EveryValue_KeepsItsPersistedOrdinal()
    {
        var drifted = ExpectedOrdinals
            .Where(pair => (int)pair.Key != pair.Value)
            .Select(pair => $"{pair.Key} is {(int)pair.Key}, expected {pair.Value}")
            .ToList();

        Assert.That(drifted, Is.Empty,
            "a Pending Export status's ordinal changed, which silently re-labels every Pending Export already persisted: " +
            string.Join("; ", drifted));
    }

    [Test]
    public void PendingExportStatus_EveryDeclaredValue_IsPinned()
    {
        var unpinned = Enum.GetValues<PendingExportStatus>()
            .Where(value => !ExpectedOrdinals.ContainsKey(value))
            .ToList();

        Assert.That(unpinned, Is.Empty,
            "new Pending Export status(es) not pinned in this test: " + string.Join(", ", unpinned) +
            ". Append them here with the ordinal they were assigned, so a later reorder is caught.");
    }
}
