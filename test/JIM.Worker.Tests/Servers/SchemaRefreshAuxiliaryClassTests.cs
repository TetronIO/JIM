// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What a schema refresh does to the attributes an administrator's auxiliary class selections contributed.
/// </summary>
/// <remarks>
/// An RFC 4512 directory attaches an auxiliary class to an entry rather than to a class, so those attributes are
/// never in the discovered schema for the structural type. A refresh that treated "not discovered" as "removed"
/// would delete and re-create them on every run, and the new rows would carry new ids: every Synchronisation Rule
/// mapping pointing at one would be left dangling, and the administrator would be told their schema had lost
/// attributes that are still there.
/// </remarks>
[TestFixture]
public class SchemaRefreshAuxiliaryClassTests
{
    private const int PersonId = 1;
    private const int PosixAccountId = 2;
    private const int ContributedAttributeId = 99;

    /// <summary>
    /// A Connected System that has already been through a refresh and had posixAccount selected on inetOrgPerson,
    /// so uidNumber is on the structural type carrying posixAccount's name.
    /// </summary>
    private static ConnectedSystem BuildConnectedSystem()
    {
        var person = new ConnectedSystemObjectType
        {
            Id = PersonId,
            Name = "inetOrgPerson",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "cn", ClassName = "inetOrgPerson", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute
                {
                    Id = ContributedAttributeId, Name = "uidNumber", ClassName = "posixAccount",
                    Type = AttributeDataType.Number, Selected = true
                }
            ]
        };

        var posixAccount = new ConnectedSystemObjectType
        {
            Id = PosixAccountId,
            Name = "posixAccount",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 20, Name = "uidNumber", ClassName = "posixAccount", Type = AttributeDataType.Number }
            ]
        };

        person.Extensions.Add(new ConnectedSystemObjectTypeExtension
        {
            BaseObjectType = person,
            BaseObjectTypeId = PersonId,
            ExtensionObjectType = posixAccount,
            ExtensionObjectTypeId = PosixAccountId
        });

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Yellowstone",
            ConnectorDefinition = new ConnectorDefinition { Name = "JIM LDAP Connector" },
            ObjectTypes = [person, posixAccount]
        };
    }

    /// <summary>
    /// What the directory reports on a refresh: the structural class's own attributes, and the auxiliary class as a
    /// type in its own right. Neither mentions that inetOrgPerson entries carry uidNumber, because the directory
    /// does not know that; only the administrator's selection says so.
    /// </summary>
    private static ConnectorSchema BuildDiscoveredSchema()
    {
        var person = new ConnectorSchemaObjectType("inetOrgPerson");
        person.Attributes.Add(new ConnectorSchemaAttribute("cn", AttributeDataType.Text, AttributePlurality.SingleValued,
            required: true, className: "inetOrgPerson", writability: AttributeWritability.Writable));

        var posixAccount = new ConnectorSchemaObjectType("posixAccount");
        posixAccount.Attributes.Add(new ConnectorSchemaAttribute("uidNumber", AttributeDataType.Number, AttributePlurality.SingleValued,
            required: true, className: "posixAccount", writability: AttributeWritability.Writable));

        // RFC 4512 discovery hands every object type's class membership to JIM. The tag is what entitles the
        // reconciliation half of the merge to act on a type; without it (the Active Directory path, whose
        // discovery stamps attributes with the classes they were inherited from) the type is left alone.
        person.Tags.Add(new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassMembershipAttribute, "objectClass"));
        posixAccount.Tags.Add(new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassMembershipAttribute, "objectClass"));

        return new ConnectorSchema { ObjectTypes = [person, posixAccount] };
    }

    private static ConnectedSystemObjectType Person(ConnectedSystem connectedSystem)
    {
        return connectedSystem.ObjectTypes!.Single(objectType => objectType.Name == "inetOrgPerson");
    }

    /// <summary>
    /// A Connector reports which attributes its Connected System demands. JIM has to keep that: an export omitting
    /// one leaves the object invalid at the Connected System, and refusing it with the attribute named is only
    /// possible if the requirement survived schema import.
    /// </summary>
    [Test]
    public void MergeSchema_KeepsWhichAttributesTheConnectedSystemRequires()
    {
        var connectedSystem = BuildConnectedSystem();

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildDiscoveredSchema());

        var cn = Person(connectedSystem).Attributes!.Single(attribute => attribute.Name == "cn");
        Assert.That(cn.Required, Is.True);
    }

    /// <summary>
    /// A Connected System that stops demanding an attribute must be reflected too, or JIM would go on refusing
    /// exports it would now accept.
    /// </summary>
    [Test]
    public void MergeSchema_WhenTheConnectedSystemStopsRequiringAnAttribute_ReflectsThat()
    {
        var connectedSystem = BuildConnectedSystem();
        Person(connectedSystem).Attributes!.Single(attribute => attribute.Name == "cn").Required = true;

        var schema = BuildDiscoveredSchema();
        schema.ObjectTypes.Single(objectType => objectType.Name == "inetOrgPerson")
            .Attributes.Single(attribute => attribute.Name == "cn").Required = false;

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, schema);

        Assert.That(Person(connectedSystem).Attributes!.Single(attribute => attribute.Name == "cn").Required, Is.False);
    }

    [Test]
    public void MergeSchema_ForAnAttributeAnAuxiliaryClassContributed_KeepsTheSameAttributeRow()
    {
        var connectedSystem = BuildConnectedSystem();

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildDiscoveredSchema());

        var uidNumber = Person(connectedSystem).Attributes!.Single(attribute => attribute.Name == "uidNumber");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(uidNumber.Id, Is.EqualTo(ContributedAttributeId),
                "a new row would leave every Synchronisation Rule mapping referencing this attribute dangling");
            Assert.That(uidNumber.Selected, Is.True, "the administrator's selection is theirs, and a refresh is not a reason to drop it");
        }
    }

    [Test]
    public void MergeSchema_DoesNotReportAContributedAttributeAsRemoved()
    {
        // It is not in the discovered schema and never will be, so a naive comparison calls it removed on every
        // single refresh: the administrator would be warned about losing an attribute that is still there.
        var connectedSystem = BuildConnectedSystem();

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildDiscoveredSchema());

        Assert.That(result.RemovedAttributes.TryGetValue("inetOrgPerson", out var removed) ? removed : [],
            Does.Not.Contain("uidNumber"));
    }

    [Test]
    public void MergeSchema_WhenTheDirectoryStopsPublishingTheAuxiliaryClass_TakesItsContributionWithIt()
    {
        // The selection cannot survive the class it points at. The database cascade says the same thing; this is
        // the in-memory half, and it must not leave an attribute behind that nothing contributes.
        var connectedSystem = BuildConnectedSystem();
        var schema = BuildDiscoveredSchema();
        schema.ObjectTypes.RemoveAll(objectType => objectType.Name == "posixAccount");

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, schema);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Person(connectedSystem).Attributes!.Select(attribute => attribute.Name), Is.EquivalentTo(new[] { "cn" }));
            Assert.That(result.RemovedObjectTypes, Does.Contain("posixAccount"));
        }
    }
}
