// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Services;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Connected System schema tab's attribute grid, which is virtualised because an Active Directory object
/// type runs to several hundred attributes.
/// </summary>
/// <remarks>
/// What is pinned here is what the windowing could silently break: the rows are the object type's own attribute
/// objects rather than copies, so a selection switch writes through to what Save Changes persists; the Selected Only
/// filter decides which objects are in the window rather than merely hiding rendered rows; and the sort applies to
/// the whole match set, not to whichever window happens to be on screen.
/// </remarks>
[TestFixture]
public class ConnectedSystemSchemaAttributeGridTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new UnusedJimApplicationFactory());
        Services.AddSingleton<IUserPreferenceService>(new FakeUserPreferenceService());
    }

    /// <summary>
    /// The tab only reaches the application layer in response to an administrator's action (retrieving schema,
    /// saving), and this fixture only renders it and drives its filters, so this exists to satisfy the injection
    /// and throws if that ever changes rather than quietly returning something unusable.
    /// </summary>
    private sealed class UnusedJimApplicationFactory : IJimApplicationFactory
    {
        public JimApplication Create() =>
            throw new InvalidOperationException("The schema tab reached the application layer while merely rendering, which it should not do.");
    }

    [Test]
    public void SchemaTab_AttributeGrid_DrawsTheActiveObjectTypesAttributes()
    {
        var connectedSystem = ConnectedSystemWithSelectedObjectType();

        var component = RenderSchemaTabOnObjectTypeSubTab(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("alpha"));
            Assert.That(component.Markup, Does.Contain("beta"));
            Assert.That(component.Markup, Does.Contain("gamma"));
        }
    }

    [Test]
    public void SchemaTab_SelectionSwitch_WritesThroughToTheAttributeSaveWouldPersist()
    {
        // The grid hands the page a window of rows rather than the whole list, so the switches must still be bound
        // to the object type's own attribute objects: anything else would report a selection the save never sees.
        var connectedSystem = ConnectedSystemWithSelectedObjectType();
        var component = RenderSchemaTabOnObjectTypeSubTab(connectedSystem);
        var beta = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "beta");

        // Rows are sorted by name by default, so the second switch in the grid belongs to "beta".
        component.FindAll("#schema-attributes-1 input[type=checkbox]")[1].Change(true);

        Assert.That(beta.Selected, Is.True);
    }

    [Test]
    public void SchemaTab_SelectedOnlyFilter_WindowsOnlyTheSelectedAttributes()
    {
        // The filter has to reach the window loader. Applied to the rendered rows alone it would leave the reader
        // scrolling a list whose length still counts attributes it is not showing.
        var connectedSystem = ConnectedSystemWithSelectedObjectType();
        connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "beta").Selected = true;
        var component = RenderSchemaTabOnObjectTypeSubTab(connectedSystem);

        SetSelectedOnlyFilter(component, true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(component.Markup, Does.Contain("beta"));
            Assert.That(component.Markup, Does.Not.Contain("alpha"));
            Assert.That(component.Markup, Does.Not.Contain("gamma"));
        }
    }

    [Test]
    public void SchemaTab_SortingByName_ReordersTheWholeMatchSet()
    {
        var connectedSystem = ConnectedSystemWithSelectedObjectType();
        var component = RenderSchemaTabOnObjectTypeSubTab(connectedSystem);

        // The grid opens sorted by name ascending, so one click on the Name header flips it to descending.
        ClickSortHeader(component, "Name");

        component.WaitForAssertion(() =>
        {
            var markup = component.Markup;
            Assert.That(markup.IndexOf("gamma", StringComparison.Ordinal),
                Is.LessThan(markup.IndexOf("alpha", StringComparison.Ordinal)));
        });
    }

    /// <summary>
    /// A schema attribute's description is raw imported directory text, so its length is the directory's business:
    /// left to wrap it makes a row as tall as it likes, in a grid whose virtualiser positions every row from one
    /// fixed height. It is clipped to one line, and the whole description stays readable on the cell itself.
    /// </summary>
    [Test]
    public void SchemaTab_AttributeDescription_IsClippedToOneLineAndKeptInFull()
    {
        const string description = "RFC 4519: the common name of the entry, as long as the directory that "
                                   + "published it cares to make it, which is not a length any column can hold.";
        var connectedSystem = ConnectedSystemWithSelectedObjectType();
        connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "alpha").Description = description;

        var component = RenderSchemaTabOnObjectTypeSubTab(connectedSystem);

        var clamped = component.FindAll(".jim-one-line").Single(e => e.TextContent.Contains("RFC 4519"));
        Assert.That(clamped.GetAttribute("title"), Is.EqualTo(description));
    }

    /// <summary>
    /// Renders the schema tab with the object type's own sub-tab active, which is where the attribute grid lives.
    /// Uses the tab's documented "?ot=" deep link rather than clicking through MudBlazor's tab markup.
    /// </summary>
    private IRenderedComponent<ConnectedSystemSchemaTab> RenderSchemaTabOnObjectTypeSubTab(ConnectedSystem connectedSystem)
    {
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/admin/connected-systems/1?t=schema&ot=User");

        return Render<ConnectedSystemSchemaTab>(parameters => parameters
            .Add(p => p.ConnectedSystem, connectedSystem));
    }

    /// <summary>
    /// Sets the All / Selected Only filter by raising the chip set's own callback, rather than by clicking chips:
    /// driving the real handler without depending on MudBlazor's generated markup (see
    /// <see cref="JimComponentTestContext"/>).
    /// </summary>
    private static void SetSelectedOnlyFilter(IRenderedComponent<ConnectedSystemSchemaTab> component, bool selectedOnly)
    {
        var chipSet = component.FindComponent<MudChipSet<bool>>();
        component.InvokeAsync(() => chipSet.Instance.SelectedValueChanged.InvokeAsync(selectedOnly)).GetAwaiter().GetResult();
    }

    private static void ClickSortHeader(IRenderedComponent<ConnectedSystemSchemaTab> component, string title)
    {
        var header = component.FindComponents<VirtualisedSortHeader>().First(h => h.Instance.Title == title);
        header.Find("span").Click();
    }

    /// <summary>
    /// A Connected System whose schema has been retrieved and whose only object type is one JIM manages, with three
    /// unselected attributes named so that ascending and descending order are distinguishable.
    /// </summary>
    private static ConnectedSystem ConnectedSystemWithSelectedObjectType()
    {
        return new ConnectedSystem
        {
            Id = 1,
            Name = "Yellowstone",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM LDAP Connector" },
            ObjectTypes =
            [
                new ConnectedSystemObjectType
                {
                    Id = 1,
                    Name = "User",
                    ConnectedSystemId = 1,
                    Selected = true,
                    Attributes =
                    [
                        Attribute(1, "alpha"),
                        Attribute(2, "beta"),
                        Attribute(3, "gamma")
                    ]
                }
            ]
        };
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued
    };
}
