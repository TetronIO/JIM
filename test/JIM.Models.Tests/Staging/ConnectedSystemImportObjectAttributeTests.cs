// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.TestSupport;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// #2521: an import attribute uses one of its nine typed value lists, so it allocates each only when first used. A Full
/// Import holds millions of these at once, and an empty list for every type it does not hold was most of their weight.
/// </summary>
[TestFixture]
public class ConnectedSystemImportObjectAttributeTests
{
    [Test]
    public void Constructor_NewAttribute_HoldsNoValueList()
    {
        var attribute = new ConnectedSystemImportObjectAttribute { Name = "displayName" };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ImportAttributeValueLists.All(), Has.Length.EqualTo(9), "every typed value list is seen by the check");
            Assert.That(ImportAttributeValueLists.Allocated(attribute), Is.Empty);
        }
    }

    [Test]
    public void HasValues_NewAttribute_AllFalseAndAllocateNothing()
    {
        var attribute = new ConnectedSystemImportObjectAttribute { Name = "displayName" };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.HasStringValues, Is.False);
            Assert.That(attribute.HasReferenceValues, Is.False);
            Assert.That(attribute.HasIntValues, Is.False);
            Assert.That(attribute.HasLongValues, Is.False);
            Assert.That(attribute.HasDecimalValues, Is.False);
            Assert.That(attribute.HasGuidValues, Is.False);
            Assert.That(attribute.HasByteValues, Is.False);
            Assert.That(attribute.HasDateTimeValues, Is.False);
            Assert.That(attribute.HasBoolValues, Is.False);
            Assert.That(ImportAttributeValueLists.Allocated(attribute), Is.Empty, "reading a Has property allocates nothing");
        }
    }

    [Test]
    public void StringValues_AddedTo_AllocatesThatListAlone()
    {
        var attribute = new ConnectedSystemImportObjectAttribute { Name = "displayName" };

        attribute.StringValues.Add("Ada Ashcroft");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.StringValues, Is.EqualTo(new[] { "Ada Ashcroft" }));
            Assert.That(attribute.HasStringValues, Is.True);
            Assert.That(ImportAttributeValueLists.Allocated(attribute), Is.EqualTo(new[] { "StringValues" }));
        }
    }

    [Test]
    public void ValueLists_EachAddedToInTurn_EachReadsBackAndReportsItsValues()
    {
        var guid = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3 };
        var attribute = new ConnectedSystemImportObjectAttribute { Name = "mixed" };

        attribute.StringValues.Add("text");
        attribute.ReferenceValues.Add("CN=Ada Ashcroft,OU=People");
        attribute.IntValues.Add(7);
        attribute.LongValues.Add(7_000_000_000L);
        attribute.DecimalValues.Add(4.25m);
        attribute.GuidValues.Add(guid);
        attribute.ByteValues.Add(bytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.HasStringValues, Is.True);
            Assert.That(attribute.HasReferenceValues, Is.True);
            Assert.That(attribute.HasIntValues, Is.True);
            Assert.That(attribute.HasLongValues, Is.True);
            Assert.That(attribute.HasDecimalValues, Is.True);
            Assert.That(attribute.HasGuidValues, Is.True);
            Assert.That(attribute.HasByteValues, Is.True);
            Assert.That(attribute.ReferenceValues, Is.EqualTo(new[] { "CN=Ada Ashcroft,OU=People" }));
            Assert.That(attribute.IntValues, Is.EqualTo(new[] { 7 }));
            Assert.That(attribute.LongValues, Is.EqualTo(new[] { 7_000_000_000L }));
            Assert.That(attribute.DecimalValues, Is.EqualTo(new[] { 4.25m }));
            Assert.That(attribute.GuidValues, Is.EqualTo(new[] { guid }));
            Assert.That(attribute.ByteValues, Is.EqualTo(new[] { bytes }));
        }
    }

    [Test]
    public void ValueLists_SetByInitialiser_HoldTheGivenListAndOnlyIt()
    {
        var guids = new List<Guid> { Guid.NewGuid() };

        var attribute = new ConnectedSystemImportObjectAttribute { Name = "objectGUID", GuidValues = guids };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.GuidValues, Is.SameAs(guids));
            Assert.That(attribute.HasGuidValues, Is.True);
            Assert.That(ImportAttributeValueLists.Allocated(attribute), Is.EqualTo(new[] { "GuidValues" }));
        }
    }

    [Test]
    public void HasValues_ListSetEmpty_IsFalse()
    {
        var attribute = new ConnectedSystemImportObjectAttribute { Name = "member", ReferenceValues = [], StringValues = [] };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.HasReferenceValues, Is.False);
            Assert.That(attribute.HasStringValues, Is.False);
            Assert.That(attribute.ReferenceValues, Is.Empty);
        }
    }

    [Test]
    public void StringValues_ReadWhenNeverSet_ReturnsAnEmptyListThatLaterReadsShare()
    {
        var attribute = new ConnectedSystemImportObjectAttribute { Name = "displayName" };

        var first = attribute.StringValues;
        first.Add("Ada Ashcroft");

        Assert.That(attribute.StringValues, Is.SameAs(first), "the list read is the attribute's own, so adding to it is kept");
    }
}
