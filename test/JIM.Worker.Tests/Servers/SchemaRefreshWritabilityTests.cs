// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What a schema refresh reports when a Connector restates an attribute's writability (#2755). A tighter
/// writability is a definition change: an Attribute Flow validated against a writable attribute may no longer be
/// able to write it. A looser one breaks nothing, so it is applied without a report.
/// </summary>
/// <remarks>
/// Writability was restated silently for years. That was harmless while every read-only attribute was one the
/// Connected System itself refused to write. A Connector can also declare read-only an attribute the system would
/// accept (the LDAP Connector does so for Exchange's own attributes), and then nothing but this report stops an
/// existing Attribute Flow writing it.
/// </remarks>
[TestFixture]
public class SchemaRefreshWritabilityTests
{
    private const int UserTypeId = 1;
    private const int HomeMdbAttributeId = 10;

    [TestCase(AttributeWritability.Writable, AttributeWritability.ReadOnly)]
    [TestCase(AttributeWritability.WritableOnCreate, AttributeWritability.ReadOnly)]
    [TestCase(AttributeWritability.Writable, AttributeWritability.WritableOnCreate)]
    public void MergeSchemaIntoConnectedSystem_WritabilityTightened_IsRecordedAsADefinitionChange(AttributeWritability stored, AttributeWritability discovered)
    {
        var connectedSystem = ExistingConnectedSystem(stored);

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(discovered));

        var change = result.ChangedAttributes.TryGetValue("user", out var changes) ? changes.SingleOrDefault() : null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change, Is.Not.Null, "A tighter writability is a definition change.");
            Assert.That(change!.AttributeName, Is.EqualTo("homeMDB"));
            Assert.That(change.Aspect, Is.EqualTo(SchemaAttributeChangeAspect.Writability));
            Assert.That(change.OldValue, Is.EqualTo(stored.ToString()));
            Assert.That(change.NewValue, Is.EqualTo(discovered.ToString()));
            Assert.That(result.HasRemovalsOrDefinitionChanges, Is.True);
            Assert.That(Attribute(connectedSystem).Writability, Is.EqualTo(discovered), "The change is applied as well as reported.");
        }
    }

    [TestCase(AttributeWritability.ReadOnly, AttributeWritability.Writable)]
    [TestCase(AttributeWritability.WritableOnCreate, AttributeWritability.Writable)]
    [TestCase(AttributeWritability.Writable, AttributeWritability.Writable)]
    public void MergeSchemaIntoConnectedSystem_WritabilityLoosenedOrUnchanged_IsAppliedWithoutAReport(AttributeWritability stored, AttributeWritability discovered)
    {
        var connectedSystem = ExistingConnectedSystem(stored);

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(discovered));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ChangedAttributes.Values.SelectMany(c => c), Is.Empty);
            Assert.That(Attribute(connectedSystem).Writability, Is.EqualTo(discovered));
        }
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystem connectedSystem) =>
        connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "homeMDB");

    private static ConnectorSchema DiscoveredSchema(AttributeWritability writability)
    {
        var user = new ConnectorSchemaObjectType("user");
        user.Attributes.Add(new ConnectorSchemaAttribute("homeMDB", AttributeDataType.Reference, AttributePlurality.SingleValued, writability: writability));
        return new ConnectorSchema { ObjectTypes = [user] };
    }

    private static ConnectedSystem ExistingConnectedSystem(AttributeWritability writability) => new()
    {
        Id = 1,
        Name = "Directory",
        ConnectorDefinition = new ConnectorDefinition(),
        ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = UserTypeId,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute
                    {
                        Id = HomeMdbAttributeId,
                        Name = "homeMDB",
                        Type = AttributeDataType.Reference,
                        Selected = true,
                        Writability = writability
                    }
                ]
            }
        ]
    };
}
