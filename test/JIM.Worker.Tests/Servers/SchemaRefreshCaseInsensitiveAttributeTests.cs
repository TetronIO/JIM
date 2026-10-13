// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Whether an attribute's values compare ignoring case is the Connector's to say (#2195), so schema import records it
/// and every refresh restates it, exactly as it does an attribute's writability.
/// </summary>
[TestFixture]
public class SchemaRefreshCaseInsensitiveAttributeTests
{
    private static ConnectorSchema DiscoveredSchema(bool distinguishedNameCaseInsensitive)
    {
        var user = new ConnectorSchemaObjectType("user");
        user.Attributes.Add(new ConnectorSchemaAttribute("distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued)
        {
            CaseInsensitive = distinguishedNameCaseInsensitive
        });
        user.Attributes.Add(new ConnectorSchemaAttribute("displayName", AttributeDataType.Text, AttributePlurality.SingleValued));
        return new ConnectorSchema { ObjectTypes = [user] };
    }

    private static ConnectedSystem ConnectedSystemWith(params ConnectedSystemObjectType[] objectTypes) => new()
    {
        Id = 1,
        Name = "Panoply",
        ConnectorDefinition = new ConnectorDefinition { Name = "JIM LDAP Connector" },
        ObjectTypes = objectTypes.ToList()
    };

    private static ConnectedSystemObjectTypeAttribute StoredAttribute(ConnectedSystem connectedSystem, string name) =>
        connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == name);

    [Test]
    public void MergeSchemaIntoConnectedSystem_NewObjectType_RecordsWhatTheConnectorDeclared()
    {
        var connectedSystem = ConnectedSystemWith();

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(distinguishedNameCaseInsensitive: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StoredAttribute(connectedSystem, "distinguishedName").CaseInsensitive, Is.True);
            Assert.That(StoredAttribute(connectedSystem, "displayName").CaseInsensitive, Is.False);
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_ExistingAttributeNowDeclaredCaseInsensitive_RestatesIt()
    {
        // A Connected System whose schema was imported before the Connector made the declaration gets it on its next
        // refresh, with no other change to the attribute it already had.
        var connectedSystem = ConnectedSystemWith(new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "user",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "distinguishedName", Type = AttributeDataType.Text, Selected = true, IsSecondaryExternalId = true },
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "displayName", Type = AttributeDataType.Text }
            ]
        });

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(distinguishedNameCaseInsensitive: true));

        var distinguishedName = StoredAttribute(connectedSystem, "distinguishedName");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(distinguishedName.CaseInsensitive, Is.True);
            Assert.That(distinguishedName.Id, Is.EqualTo(10), "The refresh updates the row Synchronisation Rules already reference.");
            Assert.That(distinguishedName.IsSecondaryExternalId, Is.True, "What the administrator decided is left alone.");
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_DeclarationWithdrawn_ClearsIt()
    {
        var connectedSystem = ConnectedSystemWith(new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "user",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 10, Name = "distinguishedName", Type = AttributeDataType.Text, CaseInsensitive = true },
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "displayName", Type = AttributeDataType.Text }
            ]
        });

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(distinguishedNameCaseInsensitive: false));

        Assert.That(StoredAttribute(connectedSystem, "distinguishedName").CaseInsensitive, Is.False);
    }
}
