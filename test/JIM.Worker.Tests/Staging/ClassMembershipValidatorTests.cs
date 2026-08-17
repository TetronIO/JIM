// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Staging;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Staging;

/// <summary>
/// The last check before an export is sent: that every class it adds will have the values that class requires.
/// </summary>
/// <remarks>
/// Adding a class obliges the object to satisfy that class's requirements. Sending the change anyway has the
/// Connected System reject it and report the failure in its own terms; refusing it here names the attributes an
/// administrator has to flow.
/// </remarks>
[TestFixture]
public class ClassMembershipValidatorTests
{
    private const int PersonTypeId = 1;

    [Test]
    public void Check_WhenAClassBeingAddedHasARequiredAttributeWithNoValue_RefusesAndNamesIt()
    {
        var connectedSystem = ConnectedSystemWith(RequiredPosixAttributes("uidNumber", "gidNumber"));
        var pendingExport = PendingExportAdding("posixAccount", writing: ("uidNumber", "5001"));

        var result = ClassMembershipValidator.Check(pendingExport, connectedSystem);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("gidNumber"));
            Assert.That(result.ErrorMessage, Does.Contain("posixAccount"));
            Assert.That(result.ErrorMessage, Does.Not.Contain("uidNumber"),
                "naming an attribute that is being written would send an administrator after the wrong thing");
        }
    }

    [Test]
    public void Check_WhenTheExportWritesEveryRequiredAttribute_Permits()
    {
        var connectedSystem = ConnectedSystemWith(RequiredPosixAttributes("uidNumber", "gidNumber"));
        var pendingExport = PendingExportAdding("posixAccount", writing: [("uidNumber", "5001"), ("gidNumber", "100")]);

        Assert.That(ClassMembershipValidator.Check(pendingExport, connectedSystem), Is.Null);
    }

    [Test]
    public void Check_WhenTheObjectAlreadyHasTheRequiredValue_Permits()
    {
        var connectedSystem = ConnectedSystemWith(RequiredPosixAttributes("uidNumber", "gidNumber"));
        var pendingExport = PendingExportAdding("posixAccount", writing: ("uidNumber", "5001"));

        var gidNumber = connectedSystem.ObjectTypes!.Single().Attributes!.Single(a => a.Name == "gidNumber");
        pendingExport.ConnectedSystemObject!.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = gidNumber, AttributeId = gidNumber.Id, StringValue = "100"
        });

        Assert.That(ClassMembershipValidator.Check(pendingExport, connectedSystem), Is.Null);
    }

    /// <summary>
    /// A change that removes the value cannot be what satisfies the requirement.
    /// </summary>
    [Test]
    public void Check_WhenTheRequiredAttributeIsBeingRemoved_Refuses()
    {
        var connectedSystem = ConnectedSystemWith(RequiredPosixAttributes("uidNumber"));
        var pendingExport = PendingExportAdding("posixAccount");
        var uidNumber = connectedSystem.ObjectTypes!.Single().Attributes!.Single(a => a.Name == "uidNumber");

        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), Attribute = uidNumber, AttributeId = uidNumber.Id,
            StringValue = "5001", ChangeType = PendingExportAttributeChangeType.Remove
        });

        var result = ClassMembershipValidator.Check(pendingExport, connectedSystem);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ErrorMessage, Does.Contain("uidNumber"));
    }

    [Test]
    public void Check_WhenNoClassIsBeingAdded_Permits()
    {
        var connectedSystem = ConnectedSystemWith(RequiredPosixAttributes("uidNumber"));
        var pendingExport = PendingExportAdding();

        Assert.That(ClassMembershipValidator.Check(pendingExport, connectedSystem), Is.Null);
    }

    [Test]
    public void Check_ForAConnectedSystemWithNoClassMembership_Permits()
    {
        // A SQL table or CSV file has no equivalent, and must be left entirely alone.
        var connectedSystem = ConnectedSystemWith(RequiredPosixAttributes("uidNumber"), declaresClassMembership: false);
        var pendingExport = PendingExportAdding("posixAccount");

        Assert.That(ClassMembershipValidator.Check(pendingExport, connectedSystem), Is.Null);
    }

    #region Fixtures

    private static List<ConnectedSystemObjectTypeAttribute> RequiredPosixAttributes(params string[] required)
    {
        var attributes = new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Id = 100, Name = "objectClass", Type = AttributeDataType.Text },
            new() { Id = 101, Name = "cn", ClassName = "inetOrgPerson", Type = AttributeDataType.Text }
        };

        var id = 200;
        foreach (var name in new[] { "uidNumber", "gidNumber", "homeDirectory" })
            attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Id = id++, Name = name, ClassName = "posixAccount", Required = required.Contains(name), Type = AttributeDataType.Text
            });

        return attributes;
    }

    private static ConnectedSystem ConnectedSystemWith(List<ConnectedSystemObjectTypeAttribute> attributes, bool declaresClassMembership = true)
    {
        var person = new ConnectedSystemObjectType { Id = PersonTypeId, Name = "inetOrgPerson", Attributes = attributes };

        if (declaresClassMembership)
            person.Tags.Add(new ConnectedSystemObjectTypeTag
            {
                Key = ObjectTypeTags.Keys.ClassMembershipAttribute, Value = "objectClass"
            });

        return new ConnectedSystem { Id = 1, Name = "Yellowstone", ObjectTypes = [person] };
    }

    private static PendingExport PendingExportAdding(string? className = null, params (string Name, string Value)[] writing)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = new ConnectedSystemObject { TypeId = PersonTypeId }
        };

        if (className != null)
            pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = 100, Name = "objectClass", Type = AttributeDataType.Text },
                AttributeId = 100,
                StringValue = className,
                ChangeType = PendingExportAttributeChangeType.Add
            });

        foreach (var (name, value) in writing)
            pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = 0, Name = name, ClassName = "posixAccount", Type = AttributeDataType.Text },
                StringValue = value,
                ChangeType = PendingExportAttributeChangeType.Update
            });

        return pendingExport;
    }

    private static PendingExport PendingExportAdding(string className, (string Name, string Value) writing)
    {
        return PendingExportAdding(className, [writing]);
    }

    #endregion
}
