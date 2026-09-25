// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// <see cref="SyncRuleMappingSourcesType"/> is derived from a mapping's configured sources by
/// <see cref="SyncRuleMapping.GetSourceType"/> and <see cref="ObjectMatchingRule.GetSourceType"/>, and it is a
/// display value read by the portal, the REST API and PowerShell. It is not persisted directly, but every display
/// sweep that switches on it must handle each member explicitly rather than falling into a default, so a reorder or
/// an unhandled new member is a defect worth catching in the same way an ordinal pin catches one. Append-only for
/// the same reason as the persisted enums in this project.
///
/// Adding a value is expected; add it to the end, and add its ordinal to the map below in the same change.
/// </summary>
[TestFixture]
public class SyncRuleMappingSourcesTypeOrdinalTests
{
    /// <summary>
    /// Every value and the ordinal it is declared as. Do not edit an existing entry; append.
    /// </summary>
    private static readonly Dictionary<SyncRuleMappingSourcesType, int> ExpectedOrdinals = new()
    {
        [SyncRuleMappingSourcesType.NotSet] = 0,
        [SyncRuleMappingSourcesType.AttributeMapping] = 1,
        [SyncRuleMappingSourcesType.ExpressionMapping] = 2,
        [SyncRuleMappingSourcesType.AdvancedMapping] = 3,

        // Unique Value Generation (#242): "Generated Value" as a mapping source type.
        [SyncRuleMappingSourcesType.GeneratedMapping] = 4
    };

    [Test]
    public void SyncRuleMappingSourcesType_EveryValue_KeepsItsOrdinal()
    {
        var drifted = ExpectedOrdinals
            .Where(pair => (int)pair.Key != pair.Value)
            .Select(pair => $"{pair.Key} is {(int)pair.Key}, expected {pair.Value}")
            .ToList();

        Assert.That(drifted, Is.Empty,
            "a mapping source type's ordinal changed: " + string.Join("; ", drifted));
    }

    [Test]
    public void SyncRuleMappingSourcesType_EveryDeclaredValue_IsPinned()
    {
        var unpinned = Enum.GetValues<SyncRuleMappingSourcesType>()
            .Where(value => !ExpectedOrdinals.ContainsKey(value))
            .ToList();

        Assert.That(unpinned, Is.Empty,
            "new mapping source type(s) not pinned in this test: " + string.Join(", ", unpinned) +
            ". Append them here with the ordinal they were assigned, and check every display sweep handles it.");
    }
}
