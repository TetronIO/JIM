// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the shape of the schema tab: how it opens, and the order of an Object Type's panels.
/// </summary>
/// <remarks>
/// The tab used to open with a permanent amber warning describing what a refresh does, before any refresh had
/// been asked for, and each Object Type led with its settings and its auxiliary class list, leaving the attribute
/// grid, the thing the tab exists for, below the fold. Both are rendering decisions with nowhere else to be
/// pinned.
/// </remarks>
[TestFixture]
public class ConnectedSystemSchemaTabLayoutTests : JimComponentTestContext
{
    private const string StatusMarker = "jim-schema-status";
    private const string RetrieveMarker = "jim-schema-retrieve";
    private const string AttributeSelectionMarker = "jim-schema-attribute-selection";
    private const string ObjectTypeSettingsMarker = "jim-schema-object-type-settings";

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
        Services.AddSingleton<IConfigurationChangePreviewStarter>(new UnusedPreviewStarter());
    }

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

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

    /// <summary>
    /// With nothing retrieved, the only thing to say is how to start, and that stays an alert with the button in
    /// it: it is a call to action, not a description.
    /// </summary>
    [Test]
    public void SchemaTab_WithNoSchema_OffersToRetrieveIt()
    {
        var component = RenderSchemaTab(ConnectedSystemWith([]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.FindAll($"[data-testid='{RetrieveMarker}']"), Has.Count.EqualTo(1));
            Assert.That(component.Find($"[data-testid='{RetrieveMarker}']").TextContent, Does.Contain("Retrieve Schema"));
            Assert.That(component.FindAll($"[data-testid='{StatusMarker}']"), Is.Empty);
        }
    }

    /// <summary>
    /// With a schema in place, the tab opens on a status line saying what it holds and offering a refresh. The
    /// warning about what a refresh does belongs to the refresh preview, which is where it is true, and to the
    /// info button for a reader who goes looking; not to a band shown on every visit.
    /// </summary>
    [Test]
    public void SchemaTab_WithASchema_OpensOnAStatusLineNotAWarningBand()
    {
        var component = RenderSchemaTab(ConnectedSystemWith(
        [
            ObjectType(1, "inetOrgPerson", selected: true),
            ObjectType(2, "groupOfNames"),
            ObjectType(3, "organizationalUnit")
        ]));

        var status = component.Find($"[data-testid='{StatusMarker}']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.TextContent, Does.Contain("3 Object Types"));
            Assert.That(status.TextContent, Does.Contain("1 selected"));
            Assert.That(status.TextContent, Does.Contain("Refresh Schema"));
            Assert.That(status.TextContent, Does.Not.Contain("before anything is applied"), "the explanation is in the info button, not on the line");
            Assert.That(component.FindAll($"[data-testid='{RetrieveMarker}']"), Is.Empty);
            Assert.That(component.FindAll("[aria-label='About Schema']"), Has.Count.EqualTo(1));
        }
    }

    /// <summary>
    /// Attribute Selection is what an administrator revisits; an Object Type's settings are set once. The grid
    /// comes first.
    /// </summary>
    [Test]
    public void SchemaTab_ForAnObjectType_PutsAttributeSelectionAboveItsSettings()
    {
        var connectedSystem = ConnectedSystemWith([ObjectType(1, "inetOrgPerson", selected: true)]);
        connectedSystem.ConnectorDefinition.SupportsUserSelectedExternalId = true;
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/admin/connected-systems/1?t=schema&ot=inetOrgPerson");

        var component = RenderSchemaTab(connectedSystem);

        var attributeSelection = component.Markup.IndexOf($"data-testid=\"{AttributeSelectionMarker}\"", StringComparison.Ordinal);
        var settings = component.Markup.IndexOf($"data-testid=\"{ObjectTypeSettingsMarker}\"", StringComparison.Ordinal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributeSelection, Is.GreaterThanOrEqualTo(0), "the attribute grid is rendered for the selected type");
            Assert.That(settings, Is.GreaterThanOrEqualTo(0), "the settings panel is rendered for the selected type");
            Assert.That(attributeSelection, Is.LessThan(settings));
            Assert.That(component.FindAll($"[data-testid='{ObjectTypeSettingsMarker}']"), Has.Count.EqualTo(1), "one settings panel, not one per setting");
        }
    }

    #region Helpers

    private IRenderedComponent<ConnectedSystemSchemaTab> RenderSchemaTab(ConnectedSystem connectedSystem) =>
        Render<ConnectedSystemSchemaTab>(parameters => parameters.Add(p => p.ConnectedSystem, connectedSystem));

    private static ConnectedSystem ConnectedSystemWith(List<ConnectedSystemObjectType> objectTypes) => new()
    {
        Id = 1,
        Name = "Yellowstone",
        ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector" },
        ObjectTypes = objectTypes
    };

    private static ConnectedSystemObjectType ObjectType(int id, string name, bool selected = false) => new()
    {
        Id = id,
        Name = name,
        ConnectedSystemId = 1,
        Selected = selected,
        Tags = [new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural }]
    };

    #endregion
}
