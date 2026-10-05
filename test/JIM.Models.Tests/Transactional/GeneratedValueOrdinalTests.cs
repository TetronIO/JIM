// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// <see cref="GeneratedValueTokenKind"/> and <see cref="RetiredGeneratedValueReason"/> are persisted by ordinal, and
/// the database trigger that retires a removed flow's values (migration <c>AddRetiredGeneratedValues</c>) reads both
/// as literals: it tests <c>"TokenKind" = 1</c> for a Sequence and writes <c>3</c> for Recalled. A reorder would
/// silently re-label stored rows and make the trigger retire the wrong flows' values, so every ordinal is pinned
/// here. Both enums are append-only; add new values to the end and to the maps below in the same change.
/// </summary>
[TestFixture]
public class GeneratedValueOrdinalTests
{
    private static readonly Dictionary<GeneratedValueTokenKind, int> ExpectedTokenKindOrdinals = new()
    {
        [GeneratedValueTokenKind.OnlyIfTaken] = 0,
        [GeneratedValueTokenKind.Sequence] = 1,
        [GeneratedValueTokenKind.Random] = 2
    };

    private static readonly Dictionary<RetiredGeneratedValueReason, int> ExpectedReasonOrdinals = new()
    {
        [RetiredGeneratedValueReason.ObjectDeleted] = 0,
        [RetiredGeneratedValueReason.Regenerated] = 1,
        [RetiredGeneratedValueReason.Superseded] = 2,
        [RetiredGeneratedValueReason.Recalled] = 3
    };

    [Test]
    public void GeneratedValueTokenKind_EveryValue_KeepsItsPersistedOrdinal()
    {
        AssertOrdinals(ExpectedTokenKindOrdinals);
    }

    [Test]
    public void RetiredGeneratedValueReason_EveryValue_KeepsItsPersistedOrdinal()
    {
        AssertOrdinals(ExpectedReasonOrdinals);
    }

    private static void AssertOrdinals<TEnum>(Dictionary<TEnum, int> expected) where TEnum : struct, Enum
    {
        var actual = Enum.GetValues<TEnum>().ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual, Has.Count.EqualTo(expected.Count),
                $"{typeof(TEnum).Name} gained or lost a value; append new values and add their ordinals here.");
            foreach (var (value, ordinal) in expected)
                Assert.That(Convert.ToInt32(value), Is.EqualTo(ordinal), $"{typeof(TEnum).Name}.{value} changed its persisted ordinal.");
        }
    }
}
