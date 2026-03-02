using System.Collections.Generic;
using JIM.Models.Activities;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

[TestFixture]
public class ActivityRunProfileExecutionItemSnapshotTests
{
    #region SnapshotCsoDisplayFields

    [Test]
    public void SnapshotCsoDisplayFields_PopulatesAllFields()
    {
        var rpei = new ActivityRunProfileExecutionItem();
        var cso = CreateCsoWithDisplayData("EMP001", "John Smith", "Person");

        rpei.SnapshotCsoDisplayFields(cso);

        Assert.That(rpei.ExternalIdSnapshot, Is.EqualTo("EMP001"));
        Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("John Smith"));
        Assert.That(rpei.ObjectTypeSnapshot, Is.EqualTo("Person"));
    }

    [Test]
    public void SnapshotCsoDisplayFields_DoesNotOverwriteExistingExternalIdSnapshot()
    {
        var rpei = new ActivityRunProfileExecutionItem { ExternalIdSnapshot = "EXISTING" };
        var cso = CreateCsoWithDisplayData("EMP001", "John Smith", "Person");

        rpei.SnapshotCsoDisplayFields(cso);

        Assert.That(rpei.ExternalIdSnapshot, Is.EqualTo("EXISTING"));
        Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("John Smith"));
        Assert.That(rpei.ObjectTypeSnapshot, Is.EqualTo("Person"));
    }

    [Test]
    public void SnapshotCsoDisplayFields_DoesNotOverwriteExistingDisplayNameSnapshot()
    {
        var rpei = new ActivityRunProfileExecutionItem { DisplayNameSnapshot = "Existing Name" };
        var cso = CreateCsoWithDisplayData("EMP001", "John Smith", "Person");

        rpei.SnapshotCsoDisplayFields(cso);

        Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("Existing Name"));
    }

    [Test]
    public void SnapshotCsoDisplayFields_DoesNotOverwriteExistingObjectTypeSnapshot()
    {
        var rpei = new ActivityRunProfileExecutionItem { ObjectTypeSnapshot = "ExistingType" };
        var cso = CreateCsoWithDisplayData("EMP001", "John Smith", "Person");

        rpei.SnapshotCsoDisplayFields(cso);

        Assert.That(rpei.ObjectTypeSnapshot, Is.EqualTo("ExistingType"));
    }

    [Test]
    public void SnapshotCsoDisplayFields_HandlesNoDisplayNameAttribute()
    {
        var rpei = new ActivityRunProfileExecutionItem();
        var cso = CreateCsoWithDisplayData("EMP001", null, "Person");

        rpei.SnapshotCsoDisplayFields(cso);

        Assert.That(rpei.ExternalIdSnapshot, Is.EqualTo("EMP001"));
        Assert.That(rpei.DisplayNameSnapshot, Is.Null);
        Assert.That(rpei.ObjectTypeSnapshot, Is.EqualTo("Person"));
    }

    [Test]
    public void SnapshotCsoDisplayFields_HandlesNullType()
    {
        var rpei = new ActivityRunProfileExecutionItem();
        var cso = CreateCsoWithDisplayData("EMP001", "John Smith", null);

        rpei.SnapshotCsoDisplayFields(cso);

        Assert.That(rpei.ExternalIdSnapshot, Is.EqualTo("EMP001"));
        Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("John Smith"));
        Assert.That(rpei.ObjectTypeSnapshot, Is.Null);
    }

    [Test]
    public void SnapshotCsoDisplayFields_HandlesEmptyAttributeValues()
    {
        var rpei = new ActivityRunProfileExecutionItem();
        var cso = new ConnectedSystemObject
        {
            Type = new ConnectedSystemObjectType { Name = "Group" },
            AttributeValues = []
        };

        rpei.SnapshotCsoDisplayFields(cso);

        Assert.That(rpei.ExternalIdSnapshot, Is.Null);
        Assert.That(rpei.DisplayNameSnapshot, Is.Null);
        Assert.That(rpei.ObjectTypeSnapshot, Is.EqualTo("Group"));
    }

    [Test]
    public void SnapshotCsoDisplayFields_DisplayNameCaseInsensitive()
    {
        var rpei = new ActivityRunProfileExecutionItem();
        var cso = CreateCsoWithDisplayNameCase("DisplayName", "Mixed Case Value", "Person");

        rpei.SnapshotCsoDisplayFields(cso);

        Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("Mixed Case Value"));
    }

    [Test]
    public void SnapshotCsoDisplayFields_CalledTwice_KeepsFirstValues()
    {
        var rpei = new ActivityRunProfileExecutionItem();
        var cso1 = CreateCsoWithDisplayData("EMP001", "First Name", "Person");
        var cso2 = CreateCsoWithDisplayData("EMP002", "Second Name", "Group");

        rpei.SnapshotCsoDisplayFields(cso1);
        rpei.SnapshotCsoDisplayFields(cso2);

        Assert.That(rpei.ExternalIdSnapshot, Is.EqualTo("EMP001"));
        Assert.That(rpei.DisplayNameSnapshot, Is.EqualTo("First Name"));
        Assert.That(rpei.ObjectTypeSnapshot, Is.EqualTo("Person"));
    }

    #endregion

    #region Test Helpers

    private static ConnectedSystemObject CreateCsoWithDisplayData(string? externalId, string? displayName, string? objectTypeName)
    {
        var externalIdAttrId = 1;
        var displayNameAttrId = 2;

        var attributeValues = new List<ConnectedSystemObjectAttributeValue>();

        if (externalId != null)
        {
            attributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = externalIdAttrId,
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = externalIdAttrId, Name = "employeeId" },
                StringValue = externalId
            });
        }

        if (displayName != null)
        {
            attributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = displayNameAttrId,
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = displayNameAttrId, Name = "displayname" },
                StringValue = displayName
            });
        }

        return new ConnectedSystemObject
        {
            ExternalIdAttributeId = externalIdAttrId,
            Type = objectTypeName != null ? new ConnectedSystemObjectType { Name = objectTypeName } : null!,
            AttributeValues = attributeValues
        };
    }

    private static ConnectedSystemObject CreateCsoWithDisplayNameCase(string displayNameAttributeName, string displayNameValue, string objectTypeName)
    {
        var displayNameAttrId = 2;

        return new ConnectedSystemObject
        {
            Type = new ConnectedSystemObjectType { Name = objectTypeName },
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue
                {
                    AttributeId = displayNameAttrId,
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = displayNameAttrId, Name = displayNameAttributeName },
                    StringValue = displayNameValue
                }
            ]
        };
    }

    #endregion
}
