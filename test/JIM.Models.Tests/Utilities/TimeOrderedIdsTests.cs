// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using NUnit.Framework;

namespace JIM.Models.Tests.Utilities;

[TestFixture]
public class TimeOrderedIdsTests
{
    private static readonly MetaverseAttribute DisplayName = new() { Id = 1, Name = "Display Name", Type = AttributeDataType.Text };

    [Test]
    public void New_Called_ReturnsTimeOrderedVersion7Id()
    {
        Assert.That(TimeOrderedIds.New().Version, Is.EqualTo(7));
    }

    [Test]
    public void AssignMissing_MetaverseObjectChangeWithNoIds_GivesEveryRowAVersion7Id()
    {
        var change = MvoChange();

        TimeOrderedIds.AssignMissing(change);

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

        TimeOrderedIds.AssignMissing(change);

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

        TimeOrderedIds.AssignMissing(change);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Id.Version, Is.EqualTo(7));
            Assert.That(attribute.Id.Version, Is.EqualTo(7));
            Assert.That(attribute.ValueChanges.Single().Id.Version, Is.EqualTo(7));
        }
    }

    [Test]
    public void AssignMissing_ConnectedSystemObjectWithNoIds_GivesTheObjectAndEachValueAVersion7Id()
    {
        var cso = new ConnectedSystemObject();
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { StringValue = "Joan" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { StringValue = "joan@panoply.local" });

        TimeOrderedIds.AssignMissing(cso);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.Id.Version, Is.EqualTo(7));
            Assert.That(cso.AttributeValues.Select(v => v.Id.Version), Is.All.EqualTo(7));
            Assert.That(cso.AttributeValues.Select(v => v.Id), Is.Unique);
        }
    }

    [Test]
    public void AssignMissing_ConnectedSystemObjectWithIds_KeepsThem()
    {
        var (objectId, valueId) = (Guid.NewGuid(), Guid.NewGuid());
        var cso = new ConnectedSystemObject { Id = objectId };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = valueId, StringValue = "Joan" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { StringValue = "joan@panoply.local" });

        TimeOrderedIds.AssignMissing(cso);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.Id, Is.EqualTo(objectId));
            Assert.That(cso.AttributeValues[0].Id, Is.EqualTo(valueId));
            Assert.That(cso.AttributeValues[1].Id.Version, Is.EqualTo(7));
        }
    }

    [Test]
    public void AssignMissing_MetaverseObjectWithNoIds_GivesTheObjectAndEachValueAVersion7Id()
    {
        var mvo = new MetaverseObject();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = DisplayName, StringValue = "Joan" });

        TimeOrderedIds.AssignMissing(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.Id.Version, Is.EqualTo(7));
            Assert.That(mvo.AttributeValues.Single().Id.Version, Is.EqualTo(7));
        }
    }

    [Test]
    public void AssignMissing_MetaverseObjectWithIds_KeepsThem()
    {
        var (objectId, valueId) = (Guid.NewGuid(), Guid.NewGuid());
        var mvo = new MetaverseObject { Id = objectId };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = valueId, Attribute = DisplayName, StringValue = "Joan" });

        TimeOrderedIds.AssignMissing(mvo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.Id, Is.EqualTo(objectId));
            Assert.That(mvo.AttributeValues.Single().Id, Is.EqualTo(valueId));
        }
    }

    [Test]
    public void AssignMissing_PendingExportWithNoIds_GivesTheExportAndEachChangeAVersion7Id()
    {
        var pendingExport = new PendingExport { ChangeType = PendingExportChangeType.Update };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange { StringValue = "Joan", ChangeType = PendingExportAttributeChangeType.Update });
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange { StringValue = "Jo", ChangeType = PendingExportAttributeChangeType.Remove });

        TimeOrderedIds.AssignMissing(pendingExport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingExport.Id.Version, Is.EqualTo(7));
            Assert.That(pendingExport.AttributeValueChanges.Select(c => c.Id.Version), Is.All.EqualTo(7));
        }
    }

    [Test]
    public void AssignMissing_PendingExportWithIds_KeepsThem()
    {
        var (exportId, changeId) = (Guid.NewGuid(), Guid.NewGuid());
        var pendingExport = new PendingExport { Id = exportId, ChangeType = PendingExportChangeType.Update };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange { Id = changeId, StringValue = "Joan" });

        TimeOrderedIds.AssignMissing(pendingExport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingExport.Id, Is.EqualTo(exportId));
            Assert.That(pendingExport.AttributeValueChanges.Single().Id, Is.EqualTo(changeId));
        }
    }

    /// <summary>
    /// The queued work the export writes in bulk takes its id when it is made: a password change and an Exchange change.
    /// </summary>
    [Test]
    public void New_QueuedPasswordAndExchangeChanges_HaveVersion7Ids()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new PendingPasswordChange().Id.Version, Is.EqualTo(7));
            Assert.That(new PendingExchangeChange { RecipientType = "UserMailbox" }.Id.Version, Is.EqualTo(7));
        }
    }

    [Test]
    public void ResolveTransientReferences_EdgeWithNoId_GivesItAVersion7Id()
    {
        var edge = new CausalEdge();

        edge.ResolveTransientReferences(TimeOrderedIds.New());

        Assert.That(edge.Id.Version, Is.EqualTo(7));
    }

    [Test]
    public void Declare_FullImport_GivesEveryPhaseAVersion7Id()
    {
        var phases = ActivityPhaseSet.Declare(TimeOrderedIds.New(), ConnectedSystemRunType.FullImport, connectorPhases: null);

        Assert.That(phases.Phases.Select(p => p.Id.Version), Is.Not.Empty.And.All.EqualTo(7));
    }

    [Test]
    public void ScopingFailureExecutionItems_OneFailure_GivesTheItemAVersion7Id()
    {
        var result = new ReferenceRecallResult();
        result.ScopingFailures.Add(new ReferenceRecallScopingFailure(TimeOrderedIds.New(), "the scope could not be decided"));

        Assert.That(result.ScopingFailureExecutionItems().Single().Id.Version, Is.EqualTo(7));
    }

    private static MetaverseObjectChange MvoChange()
    {
        var change = new MetaverseObjectChange { ChangeType = ObjectChangeType.Updated };
        change.AddAttributeValueChange(new MetaverseObjectAttributeValue { Attribute = DisplayName, StringValue = "Joan" }, ValueChangeType.Add);
        return change;
    }
}
