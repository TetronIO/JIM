// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Red-first cover for #1331. A Metaverse Object holds at most one Connected System Object per Connected
/// System, but export evaluation resolves that Object by (Metaverse Object, Connected System) alone, with
/// no Connected System Object Type in the key. An outbound Synchronisation Rule targeting a second Object
/// Type therefore resolves to whichever Object already occupies the slot, and before this guard it staged
/// a Pending Export writing its own Object Type's attribute values onto an Object of a different type.
/// Two such Rules then collided on IX_PendingExports_ConnectedSystemObjectId_Unique and killed the whole
/// synchronisation run with a raw PostgreSQL 23505.
/// </summary>
[TestFixture]
public class ExportObjectTypeConflictTests
{
    private const int ConnectedSystemId = 1;

    private static ConnectedSystemObjectType ObjectType(int id, string name) =>
        new() { Id = id, Name = name, ConnectedSystemId = ConnectedSystemId };

    private static MetaverseObject Mvo() =>
        new() { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "User" } };

    private static SyncRule ExportRule(ConnectedSystemObjectType targetType, string name = "AppUser Export") =>
        new()
        {
            Id = 5,
            Name = name,
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectType = targetType,
            ConnectedSystemObjectTypeId = targetType.Id,
            Direction = SyncRuleDirection.Export
        };

    private static ConnectedSystemObject Cso(ConnectedSystemObjectType type, Guid mvoId) =>
        new()
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            Type = type,
            TypeId = type.Id,
            MetaverseObjectId = mvoId,
            Status = ConnectedSystemObjectStatus.Normal
        };

    [Test]
    public void DetectObjectTypeConflict_ExistingObjectIsADifferentType_ReportsTheConflict()
    {
        // The shape that broke Scenario 16: the Connected System imports Person objects and joins them to
        // Metaverse Objects, so the AppUser export Rule finds a Person occupying the only slot it could use.
        var personType = ObjectType(8, "Person");
        var appUserType = ObjectType(5, "AppUser");
        var mvo = Mvo();
        var existing = Cso(personType, mvo.Id);

        var conflict = ExportEvaluationServer.DetectObjectTypeConflict(mvo, ExportRule(appUserType), existing);

        Assert.That(conflict, Is.Not.Null, "A Rule targeting AppUser must not silently export onto a Person.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(conflict!.MetaverseObjectId, Is.EqualTo(mvo.Id));
            Assert.That(conflict.SyncRuleName, Is.EqualTo("AppUser Export"));
            Assert.That(conflict.TargetObjectTypeName, Is.EqualTo("AppUser"));
            Assert.That(conflict.ExistingConnectedSystemObjectId, Is.EqualTo(existing.Id));
            Assert.That(conflict.ExistingObjectTypeName, Is.EqualTo("Person"),
                "The administrator needs both Object Type names to see why the configuration cannot be satisfied.");
        }
    }

    [Test]
    public void DetectObjectTypeConflict_ExistingObjectIsTheSameType_ReportsNothing()
    {
        // The ordinary update path: the Rule owns the Object already in the slot.
        var appUserType = ObjectType(5, "AppUser");
        var mvo = Mvo();

        var conflict = ExportEvaluationServer.DetectObjectTypeConflict(
            mvo, ExportRule(appUserType), Cso(appUserType, mvo.Id));

        Assert.That(conflict, Is.Null);
    }

    [Test]
    public void DetectObjectTypeConflict_NoExistingObject_ReportsNothing()
    {
        // The provisioning path: the slot is free, so the Rule may create its own Object in it.
        var conflict = ExportEvaluationServer.DetectObjectTypeConflict(
            Mvo(), ExportRule(ObjectType(5, "AppUser")), existingCso: null);

        Assert.That(conflict, Is.Null);
    }

    [Test]
    public void DetectObjectTypeConflict_PendingProvisioningObjectOfAnotherType_ReportsTheConflict()
    {
        // A Pending Provisioning Object still occupies the slot, so a Rule wanting a different Object Type
        // has nowhere to go. Without this the second Rule would take the provisioning path and try to create
        // a second Connected System Object for the Metaverse Object, which the join index rejects.
        var mvo = Mvo();
        var pending = Cso(ObjectType(7, "NaturalKeyAccount"), mvo.Id);
        pending.Status = ConnectedSystemObjectStatus.PendingProvisioning;

        var conflict = ExportEvaluationServer.DetectObjectTypeConflict(
            mvo, ExportRule(ObjectType(5, "AppUser")), pending);

        Assert.That(conflict, Is.Not.Null);
        Assert.That(conflict!.ExistingObjectTypeName, Is.EqualTo("NaturalKeyAccount"));
    }

    [Test]
    public void DetectObjectTypeConflict_RuleHasNoTargetObjectType_ReportsNothing()
    {
        // Missing information is not evidence of a conflict. A Synchronisation Rule whose target Object Type
        // is unset must not have every one of its exports blocked by this guard.
        var rule = ExportRule(ObjectType(5, "AppUser"));
        rule.ConnectedSystemObjectTypeId = 0;
        rule.ConnectedSystemObjectType = null!;
        var mvo = Mvo();

        var conflict = ExportEvaluationServer.DetectObjectTypeConflict(
            mvo, rule, Cso(ObjectType(8, "Person"), mvo.Id));

        Assert.That(conflict, Is.Null);
    }

    [Test]
    public void DetectObjectTypeConflict_ExistingObjectNotJoinedToThisMetaverseObject_ReportsNothing()
    {
        // An unjoined Object is not occupying anyone's slot; export matching may still claim it.
        var appUserType = ObjectType(5, "AppUser");
        var unjoined = Cso(ObjectType(8, "Person"), Guid.NewGuid());
        unjoined.MetaverseObjectId = null;

        var conflict = ExportEvaluationServer.DetectObjectTypeConflict(
            Mvo(), ExportRule(appUserType), unjoined);

        Assert.That(conflict, Is.Null);
    }
}
