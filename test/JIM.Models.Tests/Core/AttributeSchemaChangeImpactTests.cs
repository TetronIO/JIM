// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// A Metaverse Attribute's schema change is judged as a proposed (type, plurality) pair against what the attribute is
/// now (#2153, D3). Only one change survives stored values: keeping the type and going from single-valued to
/// multi-valued, because every value an object holds is still a valid member of the wider set. Every other change
/// would leave stored values of the wrong type, or more values than the attribute could hold, so values block it.
/// </summary>
[TestFixture]
public class AttributeSchemaChangeImpactTests
{
    private static AttributeSchemaChangeImpact Impact(AttributeDataType currentType, AttributePlurality currentPlurality,
        AttributeDataType proposedType, AttributePlurality proposedPlurality, int objectsWithValues) => new()
    {
        AttributeId = 42,
        AttributeName = "costCentre",
        CurrentType = currentType,
        CurrentPlurality = currentPlurality,
        ProposedType = proposedType,
        ProposedPlurality = proposedPlurality,
        TotalObjectsWithValues = objectsWithValues
    };

    [TestCase(AttributeDataType.Text)]
    [TestCase(AttributeDataType.DateTime)]
    [TestCase(AttributeDataType.Number)]
    [TestCase(AttributeDataType.Reference)]
    public void SingleValuedToMultiValuedOfTheSameType_WithStoredValues_IsALosslessWideningAndNotBlocked(AttributeDataType type)
    {
        var impact = Impact(type, AttributePlurality.SingleValued, type, AttributePlurality.MultiValued, objectsWithValues: 77);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.IsLosslessWidening, Is.True);
            Assert.That(impact.BlockedByValues, Is.False);
        }
    }

    [TestCase(AttributeDataType.Text, AttributePlurality.SingleValued, AttributeDataType.Number, AttributePlurality.SingleValued, TestName = "Type change")]
    [TestCase(AttributeDataType.Text, AttributePlurality.SingleValued, AttributeDataType.Number, AttributePlurality.MultiValued, TestName = "Type change while widening")]
    [TestCase(AttributeDataType.Text, AttributePlurality.MultiValued, AttributeDataType.Text, AttributePlurality.SingleValued, TestName = "Multi-valued to single-valued")]
    [TestCase(AttributeDataType.Text, AttributePlurality.MultiValued, AttributeDataType.Number, AttributePlurality.MultiValued, TestName = "Type change on a multi-valued attribute")]
    public void AnyOtherChange_WithStoredValues_IsNotAWideningAndIsBlocked(AttributeDataType currentType, AttributePlurality currentPlurality,
        AttributeDataType proposedType, AttributePlurality proposedPlurality)
    {
        var impact = Impact(currentType, currentPlurality, proposedType, proposedPlurality, objectsWithValues: 77);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.IsLosslessWidening, Is.False);
            Assert.That(impact.BlockedByValues, Is.True);
        }
    }

    /// <summary>
    /// Submitting the attribute's current type and plurality asks for no change at all, so stored values have nothing
    /// to protect and must not block it. It is not a widening either: nothing is widened.
    /// </summary>
    [TestCase(AttributeDataType.Text, AttributePlurality.SingleValued)]
    [TestCase(AttributeDataType.Text, AttributePlurality.MultiValued)]
    [TestCase(AttributeDataType.Reference, AttributePlurality.MultiValued)]
    [TestCase(AttributeDataType.Boolean, AttributePlurality.SingleValued)]
    public void UnchangedTypeAndPlurality_WithStoredValues_IsUnchangedAndNotBlocked(AttributeDataType type, AttributePlurality plurality)
    {
        var impact = Impact(type, plurality, type, plurality, objectsWithValues: 77);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(impact.IsUnchanged, Is.True);
            Assert.That(impact.IsLosslessWidening, Is.False);
            Assert.That(impact.BlockedByValues, Is.False);
        }
    }

    [TestCase(AttributeDataType.Text, AttributePlurality.SingleValued, AttributeDataType.Text, AttributePlurality.MultiValued, TestName = "Widening is a change")]
    [TestCase(AttributeDataType.Text, AttributePlurality.MultiValued, AttributeDataType.Text, AttributePlurality.SingleValued, TestName = "Narrowing is a change")]
    [TestCase(AttributeDataType.Text, AttributePlurality.SingleValued, AttributeDataType.Number, AttributePlurality.SingleValued, TestName = "A type change is a change")]
    public void AnyDifferentTypeOrPlurality_IsNotUnchanged(AttributeDataType currentType, AttributePlurality currentPlurality,
        AttributeDataType proposedType, AttributePlurality proposedPlurality)
    {
        var impact = Impact(currentType, currentPlurality, proposedType, proposedPlurality, objectsWithValues: 77);

        Assert.That(impact.IsUnchanged, Is.False);
    }

    [Test]
    public void AnyChange_WithNoStoredValues_IsNotBlocked()
    {
        var impact = Impact(AttributeDataType.Text, AttributePlurality.MultiValued, AttributeDataType.Number, AttributePlurality.SingleValued, objectsWithValues: 0);

        Assert.That(impact.BlockedByValues, Is.False);
    }

    [Test]
    public void IsLosslessWideningChange_AgreesWithTheImpactForEveryPair()
    {
        var types = Enum.GetValues<AttributeDataType>();
        var pluralities = Enum.GetValues<AttributePlurality>();

        var disagreements =
            from currentType in types
            from currentPlurality in pluralities
            from proposedType in types
            from proposedPlurality in pluralities
            let impact = Impact(currentType, currentPlurality, proposedType, proposedPlurality, objectsWithValues: 1)
            where AttributeSchemaChangeImpact.IsLosslessWideningChange(currentType, currentPlurality, proposedType, proposedPlurality) != impact.IsLosslessWidening
            select $"{currentType}/{currentPlurality} -> {proposedType}/{proposedPlurality}";

        Assert.That(disagreements, Is.Empty);
    }
}
