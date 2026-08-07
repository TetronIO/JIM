// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the schema tab's handling of object types a Connected System classified as internal.
/// </summary>
/// <remarks>
/// This is a component test because the behaviour it guards is a rendering decision: which of the discovered object
/// types the discovery grid draws. The classification itself is decided in <c>ConnectedSystemObjectTypeTagExtensions</c>
/// and covered there; what cannot be tested anywhere else is that the grid honours it, that it never withholds a type
/// the administrator has selected, and that it says how many it is holding back rather than quietly shortening the
/// list.
/// </remarks>
[TestFixture]
public class ConnectedSystemSchemaTabInternalTypeTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
    }

    /// <summary>
    /// The tab only reaches the application layer in response to an administrator's action (retrieving schema,
    /// saving), and this fixture only renders it, so this exists to satisfy the injection and throws if that ever
    /// changes rather than quietly returning something unusable.
    /// </summary>
    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The schema tab reached the application layer while merely rendering, which it should not do.");
    }

    [Test]
    public void SchemaTab_ByDefault_DoesNotDrawInternalObjectTypes()
    {
        var component = RenderSchemaTab(ConnectedSystemWithDiscoveredSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("inetOrgPerson"), "An object type an administrator manages must always be drawn.");
            Assert.That(component.Markup, Does.Not.Contain("olcGlobal"));
            Assert.That(component.Markup, Does.Not.Contain("auditAdd"));
        }
    }

    [Test]
    public void SchemaTab_ByDefault_SaysHowManyInternalObjectTypesItIsHoldingBack()
    {
        // A shorter list with no explanation reads as "this is everything the directory has", which is exactly the
        // misunderstanding this feature must not create.
        var component = RenderSchemaTab(ConnectedSystemWithDiscoveredSchema());

        Assert.That(component.Markup, Does.Contain("2 internal object types are hidden"));
    }

    [Test]
    public void SchemaTab_WhenInternalObjectTypesAreAskedFor_DrawsThemAll()
    {
        var component = RenderSchemaTab(ConnectedSystemWithDiscoveredSchema());

        component.Find("button:contains('Show internal object types')").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("olcGlobal"));
            Assert.That(component.Markup, Does.Contain("auditAdd"));
            Assert.That(component.Markup, Does.Contain("inetOrgPerson"));
        }
    }

    [Test]
    public void SchemaTab_ForAnInternalObjectTypeTheAdministratorSelected_DrawsItAnyway()
    {
        // Someone has deliberately chosen to manage this class. Hiding it would leave them unable to see, or
        // deselect, a choice they made.
        var connectedSystem = ConnectedSystemWithDiscoveredSchema();
        connectedSystem.ObjectTypes!.Single(ot => ot.Name == "auditAdd").Selected = true;

        var component = RenderSchemaTab(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("auditAdd"));
            Assert.That(component.Markup, Does.Not.Contain("olcGlobal"), "The other internal object type is not selected, so it stays hidden.");
            Assert.That(component.Markup, Does.Contain("1 internal object type is hidden"), "The selected type is shown, so only one is being held back.");
        }
    }

    [Test]
    public void SchemaTab_ForAConnectedSystemThatClassifiesNothing_DrawsEveryObjectTypeAndSaysNothingAboutInternalTypes()
    {
        // The File and SCIM connectors report no classification at all. An unclassified object type is shown, and
        // the disclosure must not appear where there is nothing to disclose.
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "Test Connector" },
            ObjectTypes =
            [
                ObjectType(1, "User"),
                ObjectType(2, "Group")
            ]
        };

        var component = RenderSchemaTab(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("User"));
            Assert.That(component.Markup, Does.Contain("Group"));
            Assert.That(component.Markup, Does.Not.Contain("internal object type"));
        }
    }

    private IRenderedComponent<ConnectedSystemSchemaTab> RenderSchemaTab(ConnectedSystem connectedSystem)
    {
        return Render<ConnectedSystemSchemaTab>(parameters => parameters
            .Add(p => p.ConnectedSystem, connectedSystem));
    }

    /// <summary>
    /// A Connected System shaped like a stock OpenLDAP after schema discovery: one class an administrator manages,
    /// and two the directory keeps for itself.
    /// </summary>
    private static ConnectedSystem ConnectedSystemWithDiscoveredSchema()
    {
        return new ConnectedSystem
        {
            Id = 1,
            Name = "Yellowstone",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector" },
            ObjectTypes =
            [
                ObjectType(1, "inetOrgPerson", ObjectTypeTags.Values.ClassKindStructural),
                ObjectType(2, "olcGlobal", ObjectTypeTags.Values.ClassKindStructural, isInternal: true),
                ObjectType(3, "auditAdd", ObjectTypeTags.Values.ClassKindStructural, isInternal: true)
            ]
        };
    }

    private static ConnectedSystemObjectType ObjectType(int id, string name, string? classKind = null, bool isInternal = false)
    {
        var tags = new List<ConnectedSystemObjectTypeTag>();

        if (classKind != null)
            tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = classKind });

        if (isInternal)
            tags.Add(new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.Visibility, Value = ObjectTypeTags.Values.VisibilityInternal });

        return new ConnectedSystemObjectType
        {
            Id = id,
            Name = name,
            ConnectedSystemId = 1,
            Tags = tags
        };
    }
}
