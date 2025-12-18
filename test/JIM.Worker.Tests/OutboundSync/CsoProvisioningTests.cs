using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for CSO provisioning - creating CSO with PendingProvisioning status during export evaluation.
/// </summary>
[TestFixture]
public class CsoProvisioningTests
{
    #region PendingProvisioning Status Tests

    [Test]
    public void ConnectedSystemObjectStatus_PendingProvisioning_HasCorrectValue()
    {
        // Assert
        Assert.That((int)ConnectedSystemObjectStatus.PendingProvisioning, Is.EqualTo(2));
    }

    [Test]
    public void ConnectedSystemObjectStatus_AllValuesAreDefined()
    {
        // Assert
        Assert.That(Enum.IsDefined(typeof(ConnectedSystemObjectStatus), 0), Is.True, "Normal (0) should be defined");
        Assert.That(Enum.IsDefined(typeof(ConnectedSystemObjectStatus), 1), Is.True, "Obsolete (1) should be defined");
        Assert.That(Enum.IsDefined(typeof(ConnectedSystemObjectStatus), 2), Is.True, "PendingProvisioning (2) should be defined");
    }

    [Test]
    public void PendingProvisioningCso_CanBeCreated()
    {
        // Arrange & Act
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            TypeId = 1,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = Guid.NewGuid(),
            DateJoined = DateTime.UtcNow,
            Created = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        // Assert
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));
        Assert.That(cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned));
        Assert.That(cso.MetaverseObjectId, Is.Not.Null);
        Assert.That(cso.DateJoined, Is.Not.Null);
    }

    [Test]
    public void PendingProvisioningCso_TransitionsToNormal()
    {
        // Arrange
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned
        };

        // Act - simulate what ExportExecutionServer does after successful export
        if (cso.Status == ConnectedSystemObjectStatus.PendingProvisioning)
        {
            cso.Status = ConnectedSystemObjectStatus.Normal;
        }

        // Assert
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
        Assert.That(cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned));
    }

    #endregion

    #region JoinType Tests

    [Test]
    public void ConnectedSystemObjectJoinType_Provisioned_HasCorrectValue()
    {
        // Assert - Provisioned = 2 (NotJoined=0, Projected=1, Provisioned=2, Joined=3)
        Assert.That((int)ConnectedSystemObjectJoinType.Provisioned, Is.EqualTo(2));
    }

    [Test]
    public void ConnectedSystemObjectJoinType_Joined_HasCorrectValue()
    {
        // Assert - Joined = 3 (NotJoined=0, Projected=1, Provisioned=2, Joined=3)
        Assert.That((int)ConnectedSystemObjectJoinType.Joined, Is.EqualTo(3));
    }

    #endregion

    #region ObjectMatchingRuleMode Tests

    [Test]
    public void ObjectMatchingRuleMode_ConnectedSystem_IsDefault()
    {
        // Arrange
        var cs = new ConnectedSystem();

        // Assert - default value should be ConnectedSystem (0)
        Assert.That(cs.ObjectMatchingRuleMode, Is.EqualTo(ObjectMatchingRuleMode.ConnectedSystem));
    }

    [Test]
    public void ObjectMatchingRuleMode_SyncRule_HasCorrectValue()
    {
        // Assert
        Assert.That((int)ObjectMatchingRuleMode.SyncRule, Is.EqualTo(1));
    }

    [Test]
    public void ConnectedSystem_CanSetObjectMatchingRuleMode()
    {
        // Arrange
        var cs = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System"
        };

        // Act
        cs.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;

        // Assert
        Assert.That(cs.ObjectMatchingRuleMode, Is.EqualTo(ObjectMatchingRuleMode.SyncRule));
    }

    #endregion

    #region CSO External ID Tests

    [Test]
    public void Cso_ExternalIdAttribute_CanBePopulatedAfterExport()
    {
        // Arrange
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "User",
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 100, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true },
                new() { Id = 101, Name = "distinguishedName", Type = AttributeDataType.Text }
            }
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = objectType,
            TypeId = objectType.Id,
            ExternalIdAttributeId = 100, // objectGUID
            SecondaryExternalIdAttributeId = 101, // distinguishedName
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        // Act - simulate receiving external ID from connector after export
        var exportedGuid = Guid.NewGuid();
        var exportedDn = "CN=John Smith,OU=Users,DC=example,DC=com";

        // Add external ID attribute value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            AttributeId = cso.ExternalIdAttributeId,
            GuidValue = exportedGuid
        });

        // Add secondary external ID attribute value
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            AttributeId = cso.SecondaryExternalIdAttributeId!.Value,
            StringValue = exportedDn
        });

        // Transition to Normal status
        cso.Status = ConnectedSystemObjectStatus.Normal;

        // Assert
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
        Assert.That(cso.AttributeValues, Has.Count.EqualTo(2));

        var externalIdValue = cso.AttributeValues.First(av => av.AttributeId == cso.ExternalIdAttributeId);
        Assert.That(externalIdValue.GuidValue, Is.EqualTo(exportedGuid));

        var secondaryExternalIdValue = cso.AttributeValues.First(av => av.AttributeId == cso.SecondaryExternalIdAttributeId);
        Assert.That(secondaryExternalIdValue.StringValue, Is.EqualTo(exportedDn));
    }

    [Test]
    public void Cso_ExternalIdAttributeValue_Property_ReturnsCorrectValue()
    {
        // Arrange
        var objectGuidAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 100,
            Name = "objectGUID",
            Type = AttributeDataType.Guid,
            IsExternalId = true
        };

        var expectedGuid = Guid.NewGuid();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ExternalIdAttributeId = 100,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Attribute = objectGuidAttr,
                    AttributeId = 100,
                    GuidValue = expectedGuid
                }
            }
        };

        // Act
        var externalIdValue = cso.ExternalIdAttributeValue;

        // Assert
        Assert.That(externalIdValue, Is.Not.Null);
        Assert.That(externalIdValue!.GuidValue, Is.EqualTo(expectedGuid));
    }

    [Test]
    public void Cso_SecondaryExternalIdAttributeValue_Property_ReturnsCorrectValue()
    {
        // Arrange
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 101,
            Name = "distinguishedName",
            Type = AttributeDataType.Text
        };

        var expectedDn = "CN=Test,OU=Users,DC=example,DC=com";
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            SecondaryExternalIdAttributeId = 101,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Attribute = dnAttr,
                    AttributeId = 101,
                    StringValue = expectedDn
                }
            }
        };

        // Act
        var secondaryExternalIdValue = cso.SecondaryExternalIdAttributeValue;

        // Assert
        Assert.That(secondaryExternalIdValue, Is.Not.Null);
        Assert.That(secondaryExternalIdValue!.StringValue, Is.EqualTo(expectedDn));
    }

    #endregion
}
