// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Tests for <see cref="ExportTargetTypeNames"/>, which names an export outcome's target object type from the export
/// Synchronisation Rules by the most exact key a caller holds, and never by the Connected System alone.
/// </summary>
[TestFixture]
public class ExportTargetTypeNamesTests
{
    private const int DirectoryId = 7;
    private const int OtherSystemId = 8;
    private const int PersonMvType = 1;
    private const int GroupMvType = 2;

    private static readonly ConnectedSystemObjectType DirectoryGroup = new() { Id = 30, Name = "group", ConnectedSystemId = DirectoryId };
    private static readonly ConnectedSystemObjectType DirectoryUser = new() { Id = 31, Name = "user", ConnectedSystemId = DirectoryId };
    private static readonly ConnectedSystemObjectType DirectoryContact = new() { Id = 32, Name = "contact", ConnectedSystemId = DirectoryId };
    private static readonly ConnectedSystemObjectType OtherAccount = new() { Id = 40, Name = "account", ConnectedSystemId = OtherSystemId };

    private static SyncRule ExportRule(int id, ConnectedSystemObjectType type, int mvType) => new()
    {
        Id = id,
        Name = $"Rule {id}",
        Direction = SyncRuleDirection.Export,
        ConnectedSystemId = type.ConnectedSystemId,
        ConnectedSystemObjectType = type,
        ConnectedSystemObjectTypeId = type.Id,
        MetaverseObjectTypeId = mvType
    };

    // The directory exports groups first, then users: the order that used to label every object a group.
    private static ExportTargetTypeNames GroupsThenUsers() => ExportTargetTypeNames.From(
    [
        ExportRule(100, DirectoryGroup, GroupMvType),
        ExportRule(101, DirectoryUser, PersonMvType),
        ExportRule(102, OtherAccount, PersonMvType)
    ]);

    [Test]
    public void ForObjectType_KnownType_ReturnsThatTypesName()
    {
        var names = GroupsThenUsers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names.ForObjectType(DirectoryUser.Id), Is.EqualTo("user"));
            Assert.That(names.ForObjectType(DirectoryGroup.Id), Is.EqualTo("group"));
        }
    }

    [Test]
    public void ForObjectType_UnknownOrMissingType_ReturnsNull()
    {
        var names = GroupsThenUsers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names.ForObjectType(999), Is.Null);
            Assert.That(names.ForObjectType(null), Is.Null);
        }
    }

    [Test]
    public void ForSyncRule_KnownRule_ReturnsTheTypeTheRuleTargets()
    {
        var names = GroupsThenUsers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names.ForSyncRule(101), Is.EqualTo("user"));
            Assert.That(names.ForSyncRule(100), Is.EqualTo("group"));
        }
    }

    [Test]
    public void ForSyncRule_UnknownOrMissingRule_ReturnsNull()
    {
        var names = GroupsThenUsers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names.ForSyncRule(999), Is.Null);
            Assert.That(names.ForSyncRule(null), Is.Null);
        }
    }

    [Test]
    public void ForMetaverseObjectType_OneTypeExportedForThatMetaverseObjectType_ReturnsIt()
    {
        var names = GroupsThenUsers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names.ForMetaverseObjectType(DirectoryId, PersonMvType), Is.EqualTo("user"));
            Assert.That(names.ForMetaverseObjectType(DirectoryId, GroupMvType), Is.EqualTo("group"));
            Assert.That(names.ForMetaverseObjectType(OtherSystemId, PersonMvType), Is.EqualTo("account"));
        }
    }

    [Test]
    public void ForMetaverseObjectType_TwoTypesExportedForThatMetaverseObjectType_ReturnsNullRatherThanGuessing()
    {
        // People exported to the directory as both users and contacts: which one an object is cannot be told from
        // the Metaverse Object Type, so no name is better than a wrong one.
        var names = ExportTargetTypeNames.From(
        [
            ExportRule(101, DirectoryUser, PersonMvType),
            ExportRule(103, DirectoryContact, PersonMvType)
        ]);

        Assert.That(names.ForMetaverseObjectType(DirectoryId, PersonMvType), Is.Null);
    }

    [Test]
    public void ForMetaverseObjectType_TwoRulesExportingTheSameType_ReturnsIt()
    {
        var names = ExportTargetTypeNames.From(
        [
            ExportRule(101, DirectoryUser, PersonMvType),
            ExportRule(104, DirectoryUser, PersonMvType)
        ]);

        Assert.That(names.ForMetaverseObjectType(DirectoryId, PersonMvType), Is.EqualTo("user"));
    }

    [Test]
    public void ForMetaverseObjectType_NothingExportedForThatPairOrNoMetaverseObjectType_ReturnsNull()
    {
        var names = GroupsThenUsers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names.ForMetaverseObjectType(OtherSystemId, GroupMvType), Is.Null);
            Assert.That(names.ForMetaverseObjectType(DirectoryId, null), Is.Null);
        }
    }

    [Test]
    public void ForConnectedSystem_SystemExportingOneType_ReturnsIt()
    {
        Assert.That(GroupsThenUsers().ForConnectedSystem(OtherSystemId), Is.EqualTo("account"));
    }

    [Test]
    public void ForConnectedSystem_SystemExportingSeveralTypes_ReturnsNullRatherThanTheFirstRulesType()
    {
        Assert.That(GroupsThenUsers().ForConnectedSystem(DirectoryId), Is.Null,
            "The directory exports groups and users; naming its objects after the first rule is the bug this replaced");
    }

    [Test]
    public void ForConnectedSystem_UnknownSystem_ReturnsNull()
    {
        Assert.That(GroupsThenUsers().ForConnectedSystem(999), Is.Null);
    }

    [Test]
    public void Resolve_PrefersTheObjectsOwnTypeOverEveryOtherKey()
    {
        // The rule and the Metaverse Object Type both point at users; the object itself is a group.
        var name = GroupsThenUsers().Resolve(DirectoryId, objectTypeId: DirectoryGroup.Id, syncRuleId: 101, metaverseObjectTypeId: PersonMvType);

        Assert.That(name, Is.EqualTo("group"));
    }

    [Test]
    public void Resolve_FallsThroughToTheNextExactKeyWhereOneIsUnknown()
    {
        var names = GroupsThenUsers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names.Resolve(DirectoryId, objectTypeId: 999, syncRuleId: 100), Is.EqualTo("group"));
            Assert.That(names.Resolve(DirectoryId, syncRuleId: 999, metaverseObjectTypeId: PersonMvType), Is.EqualTo("user"));
            Assert.That(names.Resolve(OtherSystemId), Is.EqualTo("account"));
        }
    }

    [Test]
    public void Resolve_NoExactKeyOnASystemExportingSeveralTypes_ReturnsNull()
    {
        Assert.That(GroupsThenUsers().Resolve(DirectoryId), Is.Null);
    }

    [Test]
    public void ForPendingExport_TargetObjectLoadedWithItsType_ReturnsThatTypesName()
    {
        var pendingExport = new PendingExport
        {
            ConnectedSystemId = DirectoryId,
            ConnectedSystemObject = new ConnectedSystemObject { TypeId = DirectoryUser.Id, Type = DirectoryUser }
        };

        Assert.That(GroupsThenUsers().ForPendingExport(pendingExport, syncRuleId: 100), Is.EqualTo("user"));
    }

    [Test]
    public void ForPendingExport_TargetObjectLoadedWithoutItsType_ResolvesItsTypeId()
    {
        var pendingExport = new PendingExport
        {
            ConnectedSystemId = DirectoryId,
            ConnectedSystemObject = new ConnectedSystemObject { TypeId = DirectoryUser.Id }
        };

        Assert.That(GroupsThenUsers().ForPendingExport(pendingExport), Is.EqualTo("user"));
    }

    [Test]
    public void ForPendingExport_CreateWithNoTargetObjectLoaded_ResolvesTheRuleThatProvisionsIt()
    {
        var pendingExport = new PendingExport { ConnectedSystemId = DirectoryId, ProvisioningSyncRuleId = 101 };

        Assert.That(GroupsThenUsers().ForPendingExport(pendingExport), Is.EqualTo("user"));
    }

    [Test]
    public void ForPendingExport_UpdateWithNoTargetObjectLoaded_ResolvesTheMetaverseObjectType()
    {
        var pendingExport = new PendingExport { ConnectedSystemId = DirectoryId };

        Assert.That(GroupsThenUsers().ForPendingExport(pendingExport, metaverseObjectTypeId: GroupMvType), Is.EqualTo("group"));
    }

    [Test]
    public void ForPendingExport_DeleteNamingTheDeprovisioningRule_ResolvesThatRule()
    {
        var pendingExport = new PendingExport { ConnectedSystemId = DirectoryId };

        Assert.That(GroupsThenUsers().ForPendingExport(pendingExport, syncRuleId: 100), Is.EqualTo("group"));
    }

    [Test]
    public void From_RuleWithoutItsObjectTypeLoaded_NamesNothingForIt()
    {
        var unloaded = ExportRule(105, DirectoryUser, PersonMvType);
        unloaded.ConnectedSystemObjectType = null!;

        var names = ExportTargetTypeNames.From([unloaded]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names.ForSyncRule(105), Is.Null);
            Assert.That(names.ForObjectType(DirectoryUser.Id), Is.Null);
            Assert.That(names.ForMetaverseObjectType(DirectoryId, PersonMvType), Is.Null);
        }
    }

    [Test]
    public void From_NoRules_NamesNothing()
    {
        var names = ExportTargetTypeNames.From((IEnumerable<SyncRule>?)null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names.ForObjectType(DirectoryUser.Id), Is.Null);
            Assert.That(names.ForSyncRule(101), Is.Null);
            Assert.That(names.ForMetaverseObjectType(DirectoryId, PersonMvType), Is.Null);
        }
    }
}
