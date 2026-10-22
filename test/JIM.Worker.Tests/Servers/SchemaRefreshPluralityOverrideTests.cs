// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A plurality an administrator set survives a schema refresh (#2186), exactly as a data type override does. The File
/// Connector discovers every column single-valued, so without this every refresh would undo the administrator's
/// multi-valued attributes, and the next import would read each delimited cell as one value.
/// </summary>
[TestFixture]
public class SchemaRefreshPluralityOverrideTests
{
    private const int MemberAttributeId = 10;

    [Test]
    public void MergeSchemaIntoConnectedSystem_PluralitySetByAdministrator_KeepsItAndReportsNoChange()
    {
        var connectedSystem = ExistingConnectedSystem(AttributePlurality.MultiValued, pluralitySetByAdministrator: true);

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(AttributePlurality.SingleValued));

        var member = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "member");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(member.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(member.PluralitySetByAdministrator, Is.True);
            Assert.That(result.ChangedAttributes, Is.Empty);
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_DiscoveredPlurality_IsRestated()
    {
        var connectedSystem = ExistingConnectedSystem(AttributePlurality.MultiValued, pluralitySetByAdministrator: false);

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(AttributePlurality.SingleValued));

        var member = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "member");
        Assert.That(member.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
    }

    private static ConnectorSchema DiscoveredSchema(AttributePlurality memberPlurality)
    {
        var user = new ConnectorSchemaObjectType("User");
        user.Attributes.Add(new ConnectorSchemaAttribute("member", AttributeDataType.Text, memberPlurality));
        return new ConnectorSchema { ObjectTypes = [user] };
    }

    private static ConnectedSystem ExistingConnectedSystem(AttributePlurality memberPlurality, bool pluralitySetByAdministrator) => new()
    {
        Id = 1,
        Name = "HR File",
        ConnectorDefinition = new ConnectorDefinition(),
        ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 1,
                Name = "User",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute
                    {
                        Id = MemberAttributeId,
                        Name = "member",
                        Type = AttributeDataType.Text,
                        AttributePlurality = memberPlurality,
                        PluralitySetByAdministrator = pluralitySetByAdministrator,
                        Selected = true
                    }
                ]
            }
        ]
    };
}
