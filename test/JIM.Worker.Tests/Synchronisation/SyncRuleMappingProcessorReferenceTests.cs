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
}
