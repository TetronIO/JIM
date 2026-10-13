// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What a schema refresh does when the Connector reports an Object Type or attribute JIM already holds under a name
/// that differs only in case (#2051). Configuration names ignore case, so it is the same Object Type or attribute: it
/// keeps its id, and with it every Synchronisation Rule mapping and Object Matching Rule that points at it, and takes
/// the Connector's casing. Treating it as a removal and an addition orphaned those mappings.
/// </summary>
[TestFixture]
public class SchemaRefreshCaseOnlyRenameTests
{
    private const int UserTypeId = 1;
    private const int MailAttributeId = 11;
    private const int ObjectGuidAttributeId = 12;

    private static ConnectedSystem BuildConnectedSystem()
    {
        var user = new ConnectedSystemObjectType
        {
            Id = UserTypeId,
            Name = "user",
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = ObjectGuidAttributeId, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = MailAttributeId, Name = "mail", Type = AttributeDataType.Text, Selected = true }
            ]
        };

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Directory",
            ConnectorDefinition = new ConnectorDefinition { Name = "JIM LDAP Connector" },
            ObjectTypes = [user]
        };
    }

    private static ConnectorSchema BuildSchema(string typeName, string guidName, string mailName)
    {
        var user = new ConnectorSchemaObjectType(typeName);
        var objectGuid = new ConnectorSchemaAttribute(guidName, AttributeDataType.Guid, AttributePlurality.SingleValued,
            writability: AttributeWritability.ReadOnly);
        user.Attributes.Add(objectGuid);
        user.Attributes.Add(new ConnectorSchemaAttribute(mailName, AttributeDataType.Text, AttributePlurality.SingleValued,
            writability: AttributeWritability.Writable));
        user.RecommendedExternalIdAttribute = objectGuid;
        return new ConnectorSchema { ObjectTypes = [user] };
    }

    [Test]
    public void MergeSchema_AttributeRenamedOnlyInCase_KeepsItsIdAndTakesTheNewCasing()
    {
        var connectedSystem = BuildConnectedSystem();

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildSchema("user", "objectGUID", "Mail"));

        var attributes = connectedSystem.ObjectTypes!.Single().Attributes;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributes.Count(a => a.Id == MailAttributeId), Is.EqualTo(1),
                "a new row would leave every mapping that references the attribute pointing at nothing");
            Assert.That(attributes.Single(a => a.Id == MailAttributeId).Name, Is.EqualTo("Mail"));
            Assert.That(attributes.Single(a => a.Id == MailAttributeId).Selected, Is.True, "the administrator's selection survives");
            Assert.That(attributes, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void MergeSchema_AttributeRenamedOnlyInCase_IsReportedAsRenamedNotAsRemovedAndAdded()
    {
        var connectedSystem = BuildConnectedSystem();

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildSchema("user", "objectGUID", "Mail"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RemovedAttributes, Is.Empty);
            Assert.That(result.AddedAttributes, Is.Empty);
            Assert.That(result.RenamedAttributes.TryGetValue("user", out var renamed) ? renamed : [],
                Has.Exactly(1).Matches<SchemaNameChange>(r => r.OldName == "mail" && r.NewName == "Mail"));
            Assert.That(result.HasChanges, Is.True, "the rename must be offered for applying, or the stored casing never changes");
            Assert.That(result.HasRemovalsOrDefinitionChanges, Is.False, "a rename breaks nothing, so it needs no destructive decision");
        }
    }

    [Test]
    public void MergeSchema_ObjectTypeRenamedOnlyInCase_KeepsItsIdAndAttributesAndIsReportedAsRenamed()
    {
        var connectedSystem = BuildConnectedSystem();

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildSchema("User", "objectGUID", "mail"));

        var type = connectedSystem.ObjectTypes!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(type.Id, Is.EqualTo(UserTypeId));
            Assert.That(type.Name, Is.EqualTo("User"));
            Assert.That(type.Selected, Is.True);
            Assert.That(type.Attributes.Select(a => a.Id), Is.EquivalentTo(new[] { ObjectGuidAttributeId, MailAttributeId }));
            Assert.That(result.RemovedObjectTypes, Is.Empty);
            Assert.That(result.AddedObjectTypes, Is.Empty);
            Assert.That(result.RenamedObjectTypes,
                Has.Exactly(1).Matches<SchemaNameChange>(r => r.OldName == "user" && r.NewName == "User"));
            Assert.That(result.HasChanges, Is.True);
        }
    }

    [Test]
    public void MergeSchema_ExternalIdRenamedOnlyInCase_StaysTheExternalId()
    {
        var connectedSystem = BuildConnectedSystem();

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildSchema("user", "objectGuid", "mail"));

        var objectGuid = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Id == ObjectGuidAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectGuid.Name, Is.EqualTo("objectGuid"));
            Assert.That(objectGuid.IsExternalId, Is.True);
        }
    }

    [Test]
    public void MergeSchema_UnchangedSchema_ReportsNoRenames()
    {
        var connectedSystem = BuildConnectedSystem();

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildSchema("user", "objectGUID", "mail"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RenamedObjectTypes, Is.Empty);
            Assert.That(result.RenamedAttributes, Is.Empty);
            Assert.That(result.HasChanges, Is.False);
        }
    }

    [Test]
    public void MergeSchema_SchemaHoldsTwoAttributesDifferingOnlyInCase_KeepsBothAsTheyWere()
    {
        // A Connected System whose own names are case-exact (a CSV with both "Mail" and "mail" columns) holds two
        // attributes. Exact matches are paired first, so each keeps its own row and neither is reported as renamed.
        var connectedSystem = BuildConnectedSystem();
        connectedSystem.ObjectTypes!.Single().Attributes.Add(
            new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "Mail", Type = AttributeDataType.Text });
        var schema = BuildSchema("user", "objectGUID", "mail");
        schema.ObjectTypes.Single().Attributes.Add(new ConnectorSchemaAttribute("Mail", AttributeDataType.Text,
            AttributePlurality.SingleValued, writability: AttributeWritability.Writable));

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, schema);

        var attributes = connectedSystem.ObjectTypes!.Single().Attributes;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributes.Single(a => a.Id == MailAttributeId).Name, Is.EqualTo("mail"));
            Assert.That(attributes.Single(a => a.Id == 13).Name, Is.EqualTo("Mail"));
            Assert.That(result.RenamedAttributes, Is.Empty);
            Assert.That(result.HasChanges, Is.False);
        }
    }

    [Test]
    public void MergeSchema_AmbiguousCaseOnlyMatch_IsNotGuessed()
    {
        // Two stored attributes differ only in case and the Connector now reports one name matching neither exactly.
        // Choosing one would be a guess, so neither is renamed: the stored ones are reported as no longer reported
        // and the new name as added, exactly as before case-only renames were recognised.
        var connectedSystem = BuildConnectedSystem();
        connectedSystem.ObjectTypes!.Single().Attributes.Add(
            new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "Mail", Type = AttributeDataType.Text });

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildSchema("user", "objectGUID", "MAIL"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RenamedAttributes, Is.Empty);
            Assert.That(result.RemovedAttributes["user"], Is.EquivalentTo(new[] { "mail", "Mail" }));
            Assert.That(result.AddedAttributes["user"], Is.EquivalentTo(new[] { "MAIL" }));
        }
    }

    [Test]
    public void MergeSchema_AttributeRenamedOnlyInCase_KeepsTheAdministratorsCaseExactDecision()
    {
        // The rename keeps the row, so a case-exact override an administrator set travels with it rather than being
        // restated from what the Connector discovered.
        var connectedSystem = BuildConnectedSystem();
        var mail = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Id == MailAttributeId);
        mail.CaseExact = true;
        mail.CaseExactSetByAdministrator = true;

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, BuildSchema("user", "objectGUID", "Mail"));

        var renamed = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Id == MailAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(renamed.Name, Is.EqualTo("Mail"));
            Assert.That(renamed.CaseExact, Is.True);
            Assert.That(renamed.CaseExactSetByAdministrator, Is.True);
        }
    }
}
