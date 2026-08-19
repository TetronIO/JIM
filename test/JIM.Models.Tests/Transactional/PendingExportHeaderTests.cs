// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// Tests for <see cref="PendingExportHeader.FromEntity"/>'s unresolved-reference surfacing
/// (issue #1398): the list view shows how many reference changes an export is still owed, so an
/// administrator can spot an export waiting on its references without opening each detail page
/// (which explains each owed reference individually).
/// </summary>
[TestFixture]
public class PendingExportHeaderTests
{
    private static PendingExport BuildPendingExport(params PendingExportAttributeValueChange[] changes) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = 1,
        ChangeType = PendingExportChangeType.Update,
        Status = PendingExportStatus.Pending,
        CreatedAt = DateTime.UtcNow,
        AttributeValueChanges = changes.ToList()
    };

    [Test]
    public void FromEntity_MixOfResolvedAndUnresolvedChanges_CountsOnlyTheUnresolvedOnes()
    {
        var entity = BuildPendingExport(
            new PendingExportAttributeValueChange { Id = Guid.NewGuid(), AttributeId = 1, StringValue = "Someone" },
            new PendingExportAttributeValueChange { Id = Guid.NewGuid(), AttributeId = 2, UnresolvedReferenceValue = Guid.NewGuid().ToString() },
            new PendingExportAttributeValueChange { Id = Guid.NewGuid(), AttributeId = 3, UnresolvedReferenceValue = Guid.NewGuid().ToString() });
        entity.HasUnresolvedReferences = true;

        var header = PendingExportHeader.FromEntity(entity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.UnresolvedReferenceCount, Is.EqualTo(2),
                "Only changes still carrying an unresolved reference value are owed; the writable change is not.");
            Assert.That(header.AttributeChangeCount, Is.EqualTo(3));
        }
    }

    [Test]
    public void FromEntity_NoUnresolvedChanges_CountsZero()
    {
        var entity = BuildPendingExport(
            new PendingExportAttributeValueChange { Id = Guid.NewGuid(), AttributeId = 1, StringValue = "Someone" });

        var header = PendingExportHeader.FromEntity(entity);

        Assert.That(header.UnresolvedReferenceCount, Is.Zero);
    }

    [Test]
    public void FromEntity_ResolvedReferenceChange_IsNotCounted()
    {
        // A resolved reference has moved its value into StringValue and cleared
        // UnresolvedReferenceValue (ExportExecutionServer.TryResolveReferencesFromLookup); it is no
        // longer owed.
        var entity = BuildPendingExport(
            new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                AttributeId = 1,
                StringValue = "CN=Manager,DC=test",
                UnresolvedReferenceValue = null,
                ResolvedReferenceCsoId = Guid.NewGuid()
            });

        var header = PendingExportHeader.FromEntity(entity);

        Assert.That(header.UnresolvedReferenceCount, Is.Zero);
    }
}
