// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Utility;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

[TestFixture]
public class ChangeRecordIdsTests
{
    private static readonly MetaverseAttribute DisplayName = new() { Id = 1, Name = "Display Name", Type = AttributeDataType.Text };

    [Test]
    public void New_Called_ReturnsTimeOrderedVersion7Id()
    {
        Assert.That(ChangeRecordIds.New().Version, Is.EqualTo(7));
    }

    [Test]
    public void AssignMissing_MetaverseObjectChangeWithNoIds_GivesEveryRowAVersion7Id()
    {
        var change = MvoChange();

        ChangeRecordIds.AssignMissing(change);

        var attribute = change.AttributeChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Id.Version, Is.EqualTo(7));
            Assert.That(attribute.Id.Version, Is.EqualTo(7));
            Assert.That(attribute.ValueChanges.Single().Id.Version, Is.EqualTo(7));
        }
    }

    [Test]
    public void AssignMissing_MetaverseObjectChangeWithIds_KeepsThem()
    {
        var change = MvoChange();
        var (changeId, attributeId, valueId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        change.Id = changeId;
        change.AttributeChanges.Single().Id = attributeId;
        change.AttributeChanges.Single().ValueChanges.Single().Id = valueId;

        ChangeRecordIds.AssignMissing(change);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Id, Is.EqualTo(changeId));
            Assert.That(change.AttributeChanges.Single().Id, Is.EqualTo(attributeId));
            Assert.That(change.AttributeChanges.Single().ValueChanges.Single().Id, Is.EqualTo(valueId));
        }
    }

    [Test]
    public void AssignMissing_ConnectedSystemObjectChangeWithNoIds_GivesEveryRowAVersion7Id()
    {
        var change = new ConnectedSystemObjectChange { ChangeType = ObjectChangeType.Updated };
        var attribute = new ConnectedSystemObjectChangeAttribute { ConnectedSystemChange = change, AttributeName = "employeeId", AttributeType = AttributeDataType.Text };
        attribute.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attribute, ValueChangeType.Add, "E1001"));
        change.AttributeChanges.Add(attribute);

        ChangeRecordIds.AssignMissing(change);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Id.Version, Is.EqualTo(7));
            Assert.That(attribute.Id.Version, Is.EqualTo(7));
            Assert.That(attribute.ValueChanges.Single().Id.Version, Is.EqualTo(7));
        }
    }

    private static MetaverseObjectChange MvoChange()
    {
        var change = new MetaverseObjectChange { ChangeType = ObjectChangeType.Updated };
        change.AddAttributeValueChange(new MetaverseObjectAttributeValue { Attribute = DisplayName, StringValue = "Joan" }, ValueChangeType.Add);
        return change;
    }
}
