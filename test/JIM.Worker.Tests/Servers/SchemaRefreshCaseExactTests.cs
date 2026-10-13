// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What a schema refresh does to an attribute's case-exact declaration (#2051): it takes what the Connector
/// discovered, unless an administrator has decided otherwise.
/// </summary>
/// <remarks>
/// The guard mirrors <see cref="ConnectedSystemObjectTypeAttribute.TypeSetByAdministrator"/>. Without it, the next
/// routine refresh would silently put back the declaration the administrator corrected, and every identity
/// comparison that follows the declaration would change underneath them.
/// </remarks>
[TestFixture]
public class SchemaRefreshCaseExactTests
{
    private const int UserTypeId = 1;
    private const int UidAttributeId = 10;
    private const int MailAttributeId = 11;

    [Test]
    public void MergeSchemaIntoConnectedSystem_NewObjectType_TakesTheDiscoveredDeclaration()
    {
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Directory", ConnectorDefinition = new ConnectorDefinition(), ObjectTypes = [] };

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(uidCaseExact: true));

        var attributes = connectedSystem.ObjectTypes!.Single().Attributes;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributes.Single(a => a.Name == "uid").CaseExact, Is.True);
            Assert.That(attributes.Single(a => a.Name == "mail").CaseExact, Is.False);
            Assert.That(attributes.Any(a => a.CaseExactSetByAdministrator), Is.False,
                "Discovery stated these; nobody chose them.");
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_NewAttributeOnExistingObjectType_TakesTheDiscoveredDeclaration()
    {
        var connectedSystem = ExistingConnectedSystem(includeUid: false);

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(uidCaseExact: true));

        var uid = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "uid");
        Assert.That(uid.CaseExact, Is.True);
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_DiscoveredDeclarationChanged_IsRefreshed()
    {
        // The Connector's reading stands where nobody overrode it: a directory whose matching rule changed, or a
        // Connector that learned to read the declaration, reaches an existing Connected System on its next refresh.
        var connectedSystem = ExistingConnectedSystem(uidCaseExact: false);

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(uidCaseExact: true));

        var uid = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "uid");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(uid.CaseExact, Is.True);
            Assert.That(uid.Id, Is.EqualTo(UidAttributeId), "The attribute keeps its id, so mappings survive.");
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_AdministratorSetDeclaration_SurvivesTheRefresh()
    {
        // The administrator knows this directory's uid ignores case, whatever its schema says. The refresh must
        // not overrule them, and the flag must outlive this refresh so the next one leaves it alone too.
        var connectedSystem = ExistingConnectedSystem(uidCaseExact: false, uidSetByAdministrator: true);

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(uidCaseExact: true));

        var uid = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "uid");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(uid.CaseExact, Is.False);
            Assert.That(uid.CaseExactSetByAdministrator, Is.True);
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_AdministratorSetDeclaration_StillRefreshesEverythingElse()
    {
        // The flag pins the declaration alone; the Connector still states the description and writability.
        var connectedSystem = ExistingConnectedSystem(uidCaseExact: false, uidSetByAdministrator: true);
        connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "uid").Description = "Stale";

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(uidCaseExact: true));

        var uid = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "uid");
        Assert.That(uid.Description, Is.EqualTo("User id"));
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_PreRefreshSnapshot_RecordsTheDeclaration()
    {
        var connectedSystem = ExistingConnectedSystem(uidCaseExact: true);

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(uidCaseExact: false));

        var snapshot = result.PreRefreshSchema.Single().Attributes;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Single(a => a.Name == "uid").CaseExact, Is.True,
                "The snapshot holds the declaration as it stood before the refresh restated it.");
            Assert.That(snapshot.Single(a => a.Name == "mail").CaseExact, Is.False);
        }
    }

    private static ConnectorSchema DiscoveredSchema(bool uidCaseExact)
    {
        var user = new ConnectorSchemaObjectType("user");
        user.Attributes.Add(new ConnectorSchemaAttribute("uid", AttributeDataType.Text, AttributePlurality.SingleValued)
        {
            Description = "User id",
            CaseExact = uidCaseExact
        });
        user.Attributes.Add(new ConnectorSchemaAttribute("mail", AttributeDataType.Text, AttributePlurality.SingleValued));
        return new ConnectorSchema { ObjectTypes = [user] };
    }

    private static ConnectedSystem ExistingConnectedSystem(bool uidCaseExact = false, bool uidSetByAdministrator = false, bool includeUid = true)
    {
        var attributes = new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Id = MailAttributeId, Name = "mail", Type = AttributeDataType.Text, Selected = true }
        };
        if (includeUid)
        {
            attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Id = UidAttributeId,
                Name = "uid",
                Type = AttributeDataType.Text,
                Selected = true,
                CaseExact = uidCaseExact,
                CaseExactSetByAdministrator = uidSetByAdministrator
            });
        }

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Directory",
            ConnectorDefinition = new ConnectorDefinition(),
            ObjectTypes = [new ConnectedSystemObjectType { Id = UserTypeId, Name = "user", Selected = true, Attributes = attributes }]
        };
    }
}
