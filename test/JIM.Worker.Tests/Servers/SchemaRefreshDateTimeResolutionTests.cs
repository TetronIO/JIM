// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What a schema refresh does to a date attribute's resolution (#2153, decision D2): it takes what the Connector
/// discovered, unless an administrator has decided otherwise, and only a DateTime attribute carries one.
/// </summary>
[TestFixture]
public class SchemaRefreshDateTimeResolutionTests
{
    private const int WhenChangedAttributeId = 10;

    [Test]
    public void MergeSchemaIntoConnectedSystem_NewObjectType_TakesTheDiscoveredResolution()
    {
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Directory", ConnectorDefinition = new ConnectorDefinition(), ObjectTypes = [] };

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(DateTimeResolution.Second));

        var attributes = connectedSystem.ObjectTypes!.Single().Attributes;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributes.Single(a => a.Name == "whenChanged").DateTimeResolution, Is.EqualTo(DateTimeResolution.Second));
            Assert.That(attributes.Single(a => a.Name == "mail").DateTimeResolution, Is.Null);
            Assert.That(attributes.Any(a => a.DateTimeResolutionSetByAdministrator), Is.False);
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_NewAttributeOnExistingObjectType_TakesTheDiscoveredResolution()
    {
        var connectedSystem = ExistingConnectedSystem(includeWhenChanged: false);

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(DateTimeResolution.Second));

        Assert.That(WhenChanged(connectedSystem).DateTimeResolution, Is.EqualTo(DateTimeResolution.Second));
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_AttributeDiscoveredBeforeResolutionsExisted_GainsTheResolution()
    {
        // An existing Connected System holds a null resolution until its schema is refreshed; the refresh brings it in.
        var connectedSystem = ExistingConnectedSystem(resolution: null);

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(DateTimeResolution.Second));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WhenChanged(connectedSystem).DateTimeResolution, Is.EqualTo(DateTimeResolution.Second));
            Assert.That(WhenChanged(connectedSystem).Id, Is.EqualTo(WhenChangedAttributeId));
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_AdministratorSetResolution_SurvivesTheRefresh()
    {
        var connectedSystem = ExistingConnectedSystem(resolution: DateTimeResolution.Millisecond, setByAdministrator: true);

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, DiscoveredSchema(DateTimeResolution.Second));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WhenChanged(connectedSystem).DateTimeResolution, Is.EqualTo(DateTimeResolution.Millisecond));
            Assert.That(WhenChanged(connectedSystem).DateTimeResolutionSetByAdministrator, Is.True);
        }
    }

    [Test]
    public void MergeSchemaIntoConnectedSystem_ResolutionOnANonDateAttribute_IsNotKept()
    {
        // A resolution only means something for a date: a Connector stating one for a Text attribute is ignored.
        var user = new ConnectorSchemaObjectType("user");
        user.Attributes.Add(new ConnectorSchemaAttribute("mail", AttributeDataType.Text, AttributePlurality.SingleValued)
        {
            DateTimeResolution = DateTimeResolution.Second
        });
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Directory", ConnectorDefinition = new ConnectorDefinition(), ObjectTypes = [] };

        ConnectedSystemServer.MergeSchemaIntoConnectedSystem(connectedSystem, new ConnectorSchema { ObjectTypes = [user] });

        Assert.That(connectedSystem.ObjectTypes!.Single().Attributes.Single().DateTimeResolution, Is.Null);
    }

    private static ConnectedSystemObjectTypeAttribute WhenChanged(ConnectedSystem connectedSystem) =>
        connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "whenChanged");

    private static ConnectorSchema DiscoveredSchema(DateTimeResolution? whenChangedResolution)
    {
        var user = new ConnectorSchemaObjectType("user");
        user.Attributes.Add(new ConnectorSchemaAttribute("whenChanged", AttributeDataType.DateTime, AttributePlurality.SingleValued)
        {
            DateTimeResolution = whenChangedResolution
        });
        user.Attributes.Add(new ConnectorSchemaAttribute("mail", AttributeDataType.Text, AttributePlurality.SingleValued));
        return new ConnectorSchema { ObjectTypes = [user] };
    }

    private static ConnectedSystem ExistingConnectedSystem(DateTimeResolution? resolution = null, bool setByAdministrator = false, bool includeWhenChanged = true)
    {
        var attributes = new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Id = 11, Name = "mail", Type = AttributeDataType.Text, Selected = true }
        };
        if (includeWhenChanged)
        {
            attributes.Add(new ConnectedSystemObjectTypeAttribute
            {
                Id = WhenChangedAttributeId,
                Name = "whenChanged",
                Type = AttributeDataType.DateTime,
                Selected = true,
                DateTimeResolution = resolution,
                DateTimeResolutionSetByAdministrator = setByAdministrator
            });
        }

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Directory",
            ConnectorDefinition = new ConnectorDefinition(),
            ObjectTypes = [new ConnectedSystemObjectType { Id = 1, Name = "user", Selected = true, Attributes = attributes }]
        };
    }
}
