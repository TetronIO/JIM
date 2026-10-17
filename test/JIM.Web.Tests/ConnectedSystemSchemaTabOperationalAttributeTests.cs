// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Core;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the schema tab's handling of attributes a Connected System classified as operational (#2153, D8): an LDAP
/// directory publishes dozens, most of no interest, so the attribute grid holds them back until asked for, never
/// withholds one an administrator selected, and says how many it is holding back.
/// </summary>
/// <remarks>
/// The rows themselves are drawn by a virtualised grid this harness does not scroll, so the tests read the
/// Selection filter's "All (n)" count, which is computed from the same filter as the rows.
/// </remarks>
[TestFixture]
public class ConnectedSystemSchemaTabOperationalAttributeTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
        Services.AddSingleton<IConfigurationChangePreviewStarter>(new UnusedPreviewStarter());
    }

    private sealed class UnusedPreviewStarter : IConfigurationChangePreviewStarter
    {
        public Task<Guid?> StartAsync(ConfigurationChangePreviewRequest request) =>
            throw new InvalidOperationException("The schema tab started a preview while merely rendering, which it should not do.");
    }

    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The schema tab reached the application layer while merely rendering, which it should not do.");
    }

    [Test]
    public void SchemaTab_ByDefault_HoldsBackOperationalAttributesAndSaysHowMany()
    {
        var component = RenderSchemaTab(ConnectedSystemWith(InetOrgPerson()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("All (3)"), "Only the three attributes that describe the object are listed.");
            Assert.That(component.Markup, Does.Contain("2 operational attributes are hidden"));
        }
    }

    [Test]
    public void SchemaTab_WhenOperationalAttributesAreAskedFor_ListsThemAll()
    {
        var component = RenderSchemaTab(ConnectedSystemWith(InetOrgPerson()));

        component.Find("button:contains('Show operational attributes')").Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("All (5)"));
            Assert.That(component.Markup, Does.Contain("Operational attributes are shown"));
        }
    }

    [Test]
    public void SchemaTab_ForAnOperationalAttributeTheAdministratorSelected_ListsItAnyway()
    {
        // Hiding a selected attribute would leave the administrator unable to see, or deselect, their own choice.
        var objectType = InetOrgPerson();
        objectType.Attributes.Single(a => a.Name == "pwdFailureTime").Selected = true;

        var component = RenderSchemaTab(ConnectedSystemWith(objectType));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("All (4)"));
            Assert.That(component.Markup, Does.Contain("1 operational attribute is hidden"));
        }
    }

    [Test]
    public void SchemaTab_ForAnObjectTypeWithNoOperationalAttributes_SaysNothingAboutThem()
    {
        var objectType = InetOrgPerson();
        objectType.Attributes.RemoveAll(a => a.IsOperational);

        var component = RenderSchemaTab(ConnectedSystemWith(objectType));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("All (3)"));
            Assert.That(component.Markup, Does.Not.Contain("operational attribute"));
        }
    }

    private IRenderedComponent<ConnectedSystemSchemaTab> RenderSchemaTab(ConnectedSystem connectedSystem)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/admin/connected-systems/1?t=schema&ot=inetOrgPerson");
        return Render<ConnectedSystemSchemaTab>(parameters => parameters.Add(p => p.ConnectedSystem, connectedSystem));
    }

    private static ConnectedSystem ConnectedSystemWith(ConnectedSystemObjectType objectType) => new()
    {
        Id = 1,
        Name = "Yellowstone",
        ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector" },
        ObjectTypes = [objectType]
    };

    private static ConnectedSystemObjectType InetOrgPerson()
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "inetOrgPerson",
            ConnectedSystemId = 1,
            Selected = true,
            Tags = [new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural }]
        };
        objectType.Attributes =
        [
            Attribute(objectType, 1, "entryUUID", "inetOrgPerson", selected: true, isExternalId: true),
            Attribute(objectType, 2, "mail", "inetOrgPerson"),
            Attribute(objectType, 3, "cn", "person"),
            Attribute(objectType, 4, "pwdFailureTime", className: null, operational: true),
            Attribute(objectType, 5, "createTimestamp", className: null, operational: true)
        ];
        return objectType;
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(
        ConnectedSystemObjectType objectType, int id, string name, string? className,
        bool selected = false, bool isExternalId = false, bool operational = false) => new()
    {
        Id = id,
        Name = name,
        ClassName = className,
        Type = operational ? AttributeDataType.DateTime : AttributeDataType.Text,
        Writability = operational ? AttributeWritability.ReadOnly : AttributeWritability.Writable,
        Selected = selected,
        IsExternalId = isExternalId,
        SelectionLocked = isExternalId,
        IsOperational = operational,
        ConnectedSystemObjectType = objectType
    };
}
