// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// The ordering over <see cref="AttributeWritability"/>. Once the enum carries three states, combining
/// two writabilities (a complex SCIM attribute's parent and child, for example) needs a defined ordering
/// rather than a two-way read-only check: ReadOnly is the most restrictive, then WritableOnCreate, then
/// Writable.
/// </summary>
[TestFixture]
public class AttributeWritabilityExtensionsTests
{
    // All nine combinations, so a future enum value cannot quietly widen what a combination yields.
    [TestCase(AttributeWritability.Writable, AttributeWritability.Writable, AttributeWritability.Writable)]
    [TestCase(AttributeWritability.Writable, AttributeWritability.WritableOnCreate, AttributeWritability.WritableOnCreate)]
    [TestCase(AttributeWritability.Writable, AttributeWritability.ReadOnly, AttributeWritability.ReadOnly)]
    [TestCase(AttributeWritability.WritableOnCreate, AttributeWritability.Writable, AttributeWritability.WritableOnCreate)]
    [TestCase(AttributeWritability.WritableOnCreate, AttributeWritability.WritableOnCreate, AttributeWritability.WritableOnCreate)]
    [TestCase(AttributeWritability.WritableOnCreate, AttributeWritability.ReadOnly, AttributeWritability.ReadOnly)]
    [TestCase(AttributeWritability.ReadOnly, AttributeWritability.Writable, AttributeWritability.ReadOnly)]
    [TestCase(AttributeWritability.ReadOnly, AttributeWritability.WritableOnCreate, AttributeWritability.ReadOnly)]
    [TestCase(AttributeWritability.ReadOnly, AttributeWritability.ReadOnly, AttributeWritability.ReadOnly)]
    public void MostRestrictive_ReturnsTheTighterOfTheTwo(
        AttributeWritability first, AttributeWritability second, AttributeWritability expected)
    {
        Assert.That(first.MostRestrictive(second), Is.EqualTo(expected));
    }

    [Test]
    public void MostRestrictive_IsCommutative()
    {
        var values = Enum.GetValues<AttributeWritability>();

        using (Assert.EnterMultipleScope())
        {
            foreach (var first in values)
                foreach (var second in values)
                    Assert.That(first.MostRestrictive(second), Is.EqualTo(second.MostRestrictive(first)),
                        $"MostRestrictive({first}, {second}) must equal MostRestrictive({second}, {first})");
        }
    }
}
