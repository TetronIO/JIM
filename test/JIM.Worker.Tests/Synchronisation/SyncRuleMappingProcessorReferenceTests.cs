using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Tests for SyncRuleMappingProcessor reference attribute handling.
/// These tests verify that reference attributes (e.g., group members) flow correctly from CSO to MVO.
/// </summary>
public class SyncRuleMappingProcessorReferenceTests
{
    private ConnectedSystemObjectType _userObjectType = null!;
    private ConnectedSystemObjectType _groupObjectType = null!;
    private MetaverseObjectType _mvoUserType = null!;
    private MetaverseObjectType _mvoGroupType = null!;
    private MetaverseAttribute _staticMembersAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _memberAttribute = null!;

    [SetUp]
    public void Setup()
    {
        // Create user object type with DN as secondary external ID
        _userObjectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true },
                new() { Id = 2, Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true }
            }
        };

        // Create group object type with member attribute (reference type)
        _memberAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 10,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };

        _groupObjectType = new ConnectedSystemObjectType
        {
            Id = 2,
            Name = "Group",
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 11, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true },
                new() { Id = 12, Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true },
                _memberAttribute
            }
        };

        // Create MVO types
        _mvoUserType = new MetaverseObjectType { Id = 1, Name = "user" };
        _mvoGroupType = new MetaverseObjectType { Id = 2, Name = "group" };

        // Create the Static Members attribute for MVO
        _staticMembersAttribute = new MetaverseAttribute
        {
            Id = 100,
            Name = "Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
    }

    [Test]
    public void Process_ReferenceAttribute_WithResolvedReferenceAndMetaverseObject_FlowsToMvo()
    {
        // Arrange
        // Create user CSO that is joined to an MVO
        var userMvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _mvoUserType
        };

        var userCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _userObjectType,
            TypeId = _userObjectType.Id,
            MetaverseObject = userMvo
        };

        // Create group MVO (the target)
        var groupMvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _mvoGroupType,
            AttributeValues = new List<MetaverseObjectAttributeValue>()
        };

        // Create group CSO with a member reference to the user CSO
        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _groupObjectType,
            TypeId = _groupObjectType.Id,
            MetaverseObject = groupMvo
        };

        // Add the member reference attribute value - this simulates what EF Core should load
        // with the correct includes: ReferenceValue and ReferenceValue.MetaverseObject
        var memberAttributeValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = _memberAttribute,
            AttributeId = _memberAttribute.Id,
            ConnectedSystemObject = groupCso,
            ReferenceValue = userCso,  // The referenced CSO
            ReferenceValueId = userCso.Id,
            UnresolvedReferenceValue = "CN=Test User,OU=Users,DC=test,DC=local"
        };
        groupCso.AttributeValues.Add(memberAttributeValue);

        // Create sync rule mapping: CSO member -> MVO Static Members
        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _staticMembersAttribute
        };
        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttribute = _memberAttribute,
            ConnectedSystemAttributeId = _memberAttribute.Id
        });

        var connectedSystemObjectTypes = new List<ConnectedSystemObjectType> { _userObjectType, _groupObjectType };

        // Act
        SyncRuleMappingProcessor.Process(groupCso, syncRuleMapping, connectedSystemObjectTypes);

        // Assert
        // The reference should have been added to the MVO's pending additions
        Assert.That(groupMvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        var addedValue = groupMvo.PendingAttributeValueAdditions.First();
        Assert.That(addedValue.Attribute, Is.EqualTo(_staticMembersAttribute));
        Assert.That(addedValue.ReferenceValue, Is.EqualTo(userMvo), "The MVO reference should point to the user's MVO");
    }

    [Test]
    public void Process_ReferenceAttribute_WithNullReferenceValue_DoesNotFlowToMvo()
    {
        // Arrange
        // This simulates the bug scenario where EF Core didn't include ReferenceValue

        var groupMvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _mvoGroupType,
            AttributeValues = new List<MetaverseObjectAttributeValue>()
        };

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _groupObjectType,
            TypeId = _groupObjectType.Id,
            MetaverseObject = groupMvo
        };

        // Add member attribute value with ReferenceValueId set but ReferenceValue null
        // This is what happens when the EF Core Include is missing
        var memberAttributeValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = _memberAttribute,
            AttributeId = _memberAttribute.Id,
            ConnectedSystemObject = groupCso,
            ReferenceValue = null,  // BUG: Navigation property not loaded
            ReferenceValueId = Guid.NewGuid(),  // But the FK is set
            UnresolvedReferenceValue = "CN=Test User,OU=Users,DC=test,DC=local"
        };
        groupCso.AttributeValues.Add(memberAttributeValue);

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _staticMembersAttribute
        };
        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttribute = _memberAttribute,
            ConnectedSystemAttributeId = _memberAttribute.Id
        });

        var connectedSystemObjectTypes = new List<ConnectedSystemObjectType> { _userObjectType, _groupObjectType };

        // Act
        SyncRuleMappingProcessor.Process(groupCso, syncRuleMapping, connectedSystemObjectTypes);

        // Assert
        // With the bug, no reference flows because ReferenceValue is null
        // The new warning logging should detect this, but the reference still won't flow
        Assert.That(groupMvo.PendingAttributeValueAdditions, Has.Count.EqualTo(0),
            "Reference should not flow when ReferenceValue navigation is null");
    }

    [Test]
    public void Process_ReferenceAttribute_WithReferenceValueButNullMetaverseObject_DoesNotFlowToMvo()
    {
        // Arrange
        // This simulates the scenario where the referenced CSO exists but isn't joined to an MVO yet

        var groupMvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _mvoGroupType,
            AttributeValues = new List<MetaverseObjectAttributeValue>()
        };

        // User CSO exists but isn't joined to an MVO
        var userCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _userObjectType,
            TypeId = _userObjectType.Id,
            MetaverseObject = null  // Not joined yet
        };

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _groupObjectType,
            TypeId = _groupObjectType.Id,
            MetaverseObject = groupMvo
        };

        // Add member reference - ReferenceValue is loaded but it has no MetaverseObject
        var memberAttributeValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = _memberAttribute,
            AttributeId = _memberAttribute.Id,
            ConnectedSystemObject = groupCso,
            ReferenceValue = userCso,  // Reference loaded
            ReferenceValueId = userCso.Id,
            UnresolvedReferenceValue = "CN=Test User,OU=Users,DC=test,DC=local"
        };
        groupCso.AttributeValues.Add(memberAttributeValue);

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _staticMembersAttribute
        };
        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttribute = _memberAttribute,
            ConnectedSystemAttributeId = _memberAttribute.Id
        });

        var connectedSystemObjectTypes = new List<ConnectedSystemObjectType> { _userObjectType, _groupObjectType };

        // Act
        SyncRuleMappingProcessor.Process(groupCso, syncRuleMapping, connectedSystemObjectTypes);

        // Assert
        // Reference should not flow because the referenced CSO isn't joined to an MVO
        Assert.That(groupMvo.PendingAttributeValueAdditions, Has.Count.EqualTo(0),
            "Reference should not flow when referenced CSO has no MetaverseObject");
    }

    [Test]
    public void Process_MultipleReferenceValues_AllResolvedReferences_AllFlowToMvo()
    {
        // Arrange
        // Create two user CSOs joined to MVOs
        var userMvo1 = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvoUserType };
        var userMvo2 = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvoUserType };

        var userCso1 = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = _userObjectType, TypeId = _userObjectType.Id, MetaverseObject = userMvo1 };
        var userCso2 = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = _userObjectType, TypeId = _userObjectType.Id, MetaverseObject = userMvo2 };

        var groupMvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _mvoGroupType,
            AttributeValues = new List<MetaverseObjectAttributeValue>()
        };

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _groupObjectType,
            TypeId = _groupObjectType.Id,
            MetaverseObject = groupMvo
        };

        // Add two member references
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = _memberAttribute,
            AttributeId = _memberAttribute.Id,
            ConnectedSystemObject = groupCso,
            ReferenceValue = userCso1,
            ReferenceValueId = userCso1.Id,
            UnresolvedReferenceValue = "CN=User1,OU=Users,DC=test,DC=local"
        });
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = _memberAttribute,
            AttributeId = _memberAttribute.Id,
            ConnectedSystemObject = groupCso,
            ReferenceValue = userCso2,
            ReferenceValueId = userCso2.Id,
            UnresolvedReferenceValue = "CN=User2,OU=Users,DC=test,DC=local"
        });

        var syncRuleMapping = new SyncRuleMapping
        {
            Id = 1,
            TargetMetaverseAttribute = _staticMembersAttribute
        };
        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttribute = _memberAttribute,
            ConnectedSystemAttributeId = _memberAttribute.Id
        });

        var connectedSystemObjectTypes = new List<ConnectedSystemObjectType> { _userObjectType, _groupObjectType };

        // Act
        SyncRuleMappingProcessor.Process(groupCso, syncRuleMapping, connectedSystemObjectTypes);

        // Assert
        Assert.That(groupMvo.PendingAttributeValueAdditions, Has.Count.EqualTo(2),
            "Should have exactly 2 pending additions (one per CSO reference value)");
        var addedMvoIds = groupMvo.PendingAttributeValueAdditions.Select(av => av.ReferenceValue?.Id).ToList();
        Assert.That(addedMvoIds, Does.Contain(userMvo1.Id));
        Assert.That(addedMvoIds, Does.Contain(userMvo2.Id));
    }

    [Test]
    public void Process_ReferenceAttribute_WithUnresolvedReferences_SkipsRemovalLogicAsync()
    {
        // Arrange
        // Simulate a group CSO with 3 member references:
        // - 1 resolved (same page) with MetaverseObject populated
        // - 2 unresolved (cross-page) with MetaverseObject = null
        // The MVO already has 2 existing references (from a previous sync).
        // Without the fix, the removal logic would incorrectly remove the 2 existing MVO
        // references because they don't appear in the resolved CSO references.

        var userMvo1 = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvoUserType };
        var userMvo2 = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvoUserType };
        var userMvo3 = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvoUserType };

        // User 1: resolved (same page)
        var userCso1 = new ConnectedSystemObject
            { Id = Guid.NewGuid(), Type = _userObjectType, TypeId = _userObjectType.Id, MetaverseObject = userMvo1 };
        // User 2: unresolved (cross-page) — has ReferenceValue but MetaverseObject is null
        var userCso2 = new ConnectedSystemObject
            { Id = Guid.NewGuid(), Type = _userObjectType, TypeId = _userObjectType.Id, MetaverseObject = null };
        // User 3: unresolved (cross-page) — has ReferenceValue but MetaverseObject is null
        var userCso3 = new ConnectedSystemObject
            { Id = Guid.NewGuid(), Type = _userObjectType, TypeId = _userObjectType.Id, MetaverseObject = null };

        // Group MVO with 2 existing references (userMvo2 and userMvo3 from a previous sync)
        var groupMvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _mvoGroupType,
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = _staticMembersAttribute.Id, Attribute = _staticMembersAttribute, ReferenceValue = userMvo2 },
                new() { Id = Guid.NewGuid(), AttributeId = _staticMembersAttribute.Id, Attribute = _staticMembersAttribute, ReferenceValue = userMvo3 }
            }
        };

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _groupObjectType,
            TypeId = _groupObjectType.Id,
            MetaverseObject = groupMvo
        };

        // Add member references: 1 resolved, 2 unresolved
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = _memberAttribute.Id, Attribute = _memberAttribute,
            ConnectedSystemObject = groupCso,
            ReferenceValue = userCso1, ReferenceValueId = userCso1.Id
        });
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = _memberAttribute.Id, Attribute = _memberAttribute,
            ConnectedSystemObject = groupCso,
            ReferenceValue = userCso2, ReferenceValueId = userCso2.Id  // MetaverseObject is null
        });
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = _memberAttribute.Id, Attribute = _memberAttribute,
            ConnectedSystemObject = groupCso,
            ReferenceValue = userCso3, ReferenceValueId = userCso3.Id  // MetaverseObject is null
        });

        var syncRuleMapping = new SyncRuleMapping { Id = 1, TargetMetaverseAttribute = _staticMembersAttribute };
        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
            { ConnectedSystemAttribute = _memberAttribute, ConnectedSystemAttributeId = _memberAttribute.Id });

        var connectedSystemObjectTypes = new List<ConnectedSystemObjectType> { _userObjectType, _groupObjectType };

        // Act
        SyncRuleMappingProcessor.Process(groupCso, syncRuleMapping, connectedSystemObjectTypes);

        // Assert
        // The resolved reference (user1) should be added
        Assert.That(groupMvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1),
            "Resolved reference should still be added");
        Assert.That(groupMvo.PendingAttributeValueAdditions[0].ReferenceValue, Is.EqualTo(userMvo1));

        // CRITICAL: The existing MVO references (user2, user3) should NOT be removed
        // because some CSO references are unresolved (cross-page). Removal is deferred
        // to the cross-page resolution pass.
        Assert.That(groupMvo.PendingAttributeValueRemovals, Has.Count.EqualTo(0),
            "Should NOT remove existing MVO references when unresolved cross-page references exist");
    }

    [Test]
    public void Process_ReferenceAttribute_WithAllResolvedReferences_RemovesObsoleteReferencesAsync()
    {
        // Arrange
        // All references are resolved. An existing MVO reference (user3) is no longer
        // present in the CSO references, so it should be removed.

        var userMvo1 = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvoUserType };
        var userMvo2 = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvoUserType };
        var userMvo3 = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvoUserType }; // obsolete

        var userCso1 = new ConnectedSystemObject
            { Id = Guid.NewGuid(), Type = _userObjectType, TypeId = _userObjectType.Id, MetaverseObject = userMvo1 };
        var userCso2 = new ConnectedSystemObject
            { Id = Guid.NewGuid(), Type = _userObjectType, TypeId = _userObjectType.Id, MetaverseObject = userMvo2 };

        // Group MVO with 3 existing references (user3 is now obsolete - not in CSO)
        var groupMvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = _mvoGroupType,
            AttributeValues = new List<MetaverseObjectAttributeValue>
            {
                new() { Id = Guid.NewGuid(), AttributeId = _staticMembersAttribute.Id, Attribute = _staticMembersAttribute, ReferenceValue = userMvo1 },
                new() { Id = Guid.NewGuid(), AttributeId = _staticMembersAttribute.Id, Attribute = _staticMembersAttribute, ReferenceValue = userMvo2 },
                new() { Id = Guid.NewGuid(), AttributeId = _staticMembersAttribute.Id, Attribute = _staticMembersAttribute, ReferenceValue = userMvo3 }
            }
        };

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = _groupObjectType,
            TypeId = _groupObjectType.Id,
            MetaverseObject = groupMvo
        };

        // Only user1 and user2 are in the CSO — user3 has been removed from the group
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = _memberAttribute.Id, Attribute = _memberAttribute,
            ConnectedSystemObject = groupCso,
            ReferenceValue = userCso1, ReferenceValueId = userCso1.Id
        });
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = _memberAttribute.Id, Attribute = _memberAttribute,
            ConnectedSystemObject = groupCso,
            ReferenceValue = userCso2, ReferenceValueId = userCso2.Id
        });

        var syncRuleMapping = new SyncRuleMapping { Id = 1, TargetMetaverseAttribute = _staticMembersAttribute };
        syncRuleMapping.Sources.Add(new SyncRuleMappingSource
            { ConnectedSystemAttribute = _memberAttribute, ConnectedSystemAttributeId = _memberAttribute.Id });

        var connectedSystemObjectTypes = new List<ConnectedSystemObjectType> { _userObjectType, _groupObjectType };

        // Act
        SyncRuleMappingProcessor.Process(groupCso, syncRuleMapping, connectedSystemObjectTypes);

        // Assert
        // No new additions (both references already exist on MVO)
        Assert.That(groupMvo.PendingAttributeValueAdditions, Has.Count.EqualTo(0),
            "No new references to add — both already exist on MVO");

        // user3 should be removed (obsolete — not in CSO)
        Assert.That(groupMvo.PendingAttributeValueRemovals, Has.Count.EqualTo(1),
            "Obsolete MVO reference should be removed when all CSO references are resolved");
        Assert.That(groupMvo.PendingAttributeValueRemovals[0].ReferenceValue, Is.EqualTo(userMvo3));
    }
}
