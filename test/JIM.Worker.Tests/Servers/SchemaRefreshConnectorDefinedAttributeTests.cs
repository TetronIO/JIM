// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A Connector-defined Attribute (#2756) may declare the Connected System attribute it is realised through, and schema
/// import persists that declaration so a save can warn when a Synchronisation Rule flows both. Like the flag itself, the
/// declaration is Connector-owned: a refresh restates it. A refresh also refuses a Connector-defined Attribute whose name
/// matches one of the system's own attributes on the same Object Type, since the two could never be told apart.
/// </summary>
[TestFixture]
public class SchemaRefreshConnectorDefinedAttributeTests
{
    [Test]
    public void MergeSchemaIntoConnectedSystem_NewObjectType_PersistsTheDeclaredSource()
    {
        var connectedSystem = NewConnectedSystem();

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, Schema("user", Identifier(), MailNickname(), ExchangeAlias()));

        var objectType = connectedSystem.ObjectTypes!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(objectType, "Exchange Alias").SourceAttributeName, Is.EqualTo("mailNickname"));
            Assert.That(Attribute(objectType, "mailNickname").SourceAttributeName, Is.Null);
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_ExistingAttributeGainsADeclaredSource_RestatesIt()
    {
        var connectedSystem = NewConnectedSystem();
        connectedSystem.ObjectTypes = [ExistingUser(sourceAttributeName: null)];

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, Schema("user", Identifier(), MailNickname(), ExchangeAlias()));

        Assert.That(Attribute(connectedSystem.ObjectTypes!.Single(), "Exchange Alias").SourceAttributeName, Is.EqualTo("mailNickname"));
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_ConnectorStopsDeclaringASource_ClearsIt()
    {
        var connectedSystem = NewConnectedSystem();
        connectedSystem.ObjectTypes = [ExistingUser(sourceAttributeName: "mailNickname")];
        var alias = ExchangeAlias();
        alias.SourceAttributeName = null;

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, Schema("user", Identifier(), MailNickname(), alias));

        Assert.That(Attribute(connectedSystem.ObjectTypes!.Single(), "Exchange Alias").SourceAttributeName, Is.Null,
            "The declaration is Connector-owned, so a refresh restates it rather than keeping one nothing stands behind.");
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_ConnectorDefinedNameMatchesASystemAttribute_RefusesNamingTheConnector()
    {
        var clash = new ConnectorSchemaAttribute("mailNickname", AttributeDataType.Text, AttributePlurality.SingleValued) { IsConnectorDefined = true };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConnectedSystemServer.MergeSchemaIntoConnectedSystem(NewConnectedSystem(), Schema("user", Identifier(), MailNickname(), clash)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("Stub Schema Connector"));
            Assert.That(exception!.Message, Does.Contain("mailNickname"));
            Assert.That(exception!.Message, Does.Contain("user"));
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_ConnectorDefinedNameDiffersOnlyInCase_Refuses()
    {
        var clash = new ConnectorSchemaAttribute("MailNickname", AttributeDataType.Text, AttributePlurality.SingleValued) { IsConnectorDefined = true };

        Assert.That(() => ConnectedSystemServer.MergeSchemaIntoConnectedSystem(NewConnectedSystem(), Schema("user", Identifier(), MailNickname(), clash)),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_ConnectorDefinedNamesThatClashWithNothing_Merges()
    {
        var connectedSystem = NewConnectedSystem();

        Assert.That(() => ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, Schema("user", Identifier(), MailNickname(), ExchangeAlias())),
            Throws.Nothing);
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_SameNameOnADifferentObjectType_Merges()
    {
        var schema = Schema("user", Identifier(), ExchangeAlias());
        var group = new ConnectorSchemaObjectType("group") { RecommendedExternalIdAttribute = Identifier() };
        group.Attributes.AddRange([Identifier(), new ConnectorSchemaAttribute("Exchange Alias", AttributeDataType.Text, AttributePlurality.SingleValued)]);
        schema.ObjectTypes.Add(group);

        Assert.That(() => ConnectedSystemServer.MergeSchemaIntoConnectedSystem(NewConnectedSystem(), schema), Throws.Nothing,
            "The guard compares names within one Object Type only.");
    }

    private static ConnectorSchemaAttribute Identifier() =>
        new("objectGUID", AttributeDataType.Guid, AttributePlurality.SingleValued, writability: AttributeWritability.ReadOnly);

    private static ConnectorSchemaAttribute MailNickname() =>
        new("mailNickname", AttributeDataType.Text, AttributePlurality.SingleValued, className: "user");

    private static ConnectorSchemaAttribute ExchangeAlias() =>
        new("Exchange Alias", AttributeDataType.Text, AttributePlurality.SingleValued)
        {
            IsConnectorDefined = true,
            SourceAttributeName = "mailNickname"
        };

    private static ConnectorSchema Schema(string objectTypeName, params ConnectorSchemaAttribute[] attributes)
    {
        var objectType = new ConnectorSchemaObjectType(objectTypeName) { RecommendedExternalIdAttribute = attributes[0] };
        objectType.Attributes.AddRange(attributes);
        var schema = new ConnectorSchema();
        schema.ObjectTypes.Add(objectType);
        return schema;
    }

    private static ConnectedSystemObjectType ExistingUser(string? sourceAttributeName)
    {
        var objectType = new ConnectedSystemObjectType { Id = 10, Name = "user", Selected = true };
        objectType.Attributes =
        [
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 20, Name = "objectGUID", Type = AttributeDataType.Guid, Writability = AttributeWritability.ReadOnly,
                Selected = true, IsExternalId = true, SelectionLocked = true, ConnectedSystemObjectType = objectType
            },
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 21, Name = "mailNickname", Type = AttributeDataType.Text, ClassName = "user", Selected = true,
                ConnectedSystemObjectType = objectType
            },
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 22, Name = "Exchange Alias", Type = AttributeDataType.Text, Selected = true, IsConnectorDefined = true,
                SourceAttributeName = sourceAttributeName, ConnectedSystemObjectType = objectType
            }
        ];
        return objectType;
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType objectType, string name) =>
        objectType.Attributes.Single(a => a.Name == name);

    private static ConnectedSystem NewConnectedSystem() => new()
    {
        Id = 1,
        Name = "Test Stub System",
        ConnectorDefinition = new ConnectorDefinition { Name = "Stub Schema Connector" }
    };
}
