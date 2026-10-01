// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using Bunit.Rendering;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Insert attribute menu under an Attribute Flow's expression (#1750, mockup B): a filter, a Metaverse group
/// (<c>mv["..."]</c>, only offered where a derived flow is possible) and a Connected System group (<c>cs["..."]</c>).
/// Metaverse attributes a save would refuse are listed but disabled, with the reason.
/// </summary>
[TestFixture]
public class InsertAttributeMenuTests : JimComponentTestContext
{
    private static readonly MetaverseAttribute AccountName = new() { Id = 1, Name = "Account Name", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute Email = new() { Id = 2, Name = "Email", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute Manager = new() { Id = 3, Name = "Manager", Type = AttributeDataType.Reference };
    private static readonly ConnectedSystemObjectTypeAttribute FirstName = new() { Id = 10, Name = "firstName", Type = AttributeDataType.Text };
    private static readonly ConnectedSystemObjectTypeAttribute Password = new() { Id = 11, Name = "userPassword", Type = AttributeDataType.Text };

    private readonly List<string> _inserted = [];

    private RenderFragment Menu(bool withMetaverse) => builder =>
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<InsertAttributeMenu>(1);
        builder.AddComponentParameter(2, nameof(InsertAttributeMenu.MetaverseAttributes), withMetaverse ? new[] { AccountName, Email, Manager } : null);
        builder.AddComponentParameter(3, nameof(InsertAttributeMenu.ConnectedSystemAttributes), new[] { FirstName, Password });
        builder.AddComponentParameter(4, nameof(InsertAttributeMenu.ConnectedSystemName), "HR");
        builder.AddComponentParameter(5, nameof(InsertAttributeMenu.TargetMetaverseAttributeId), (int?)Email.Id);
        builder.AddComponentParameter(6, nameof(InsertAttributeMenu.OnInsert), EventCallback.Factory.Create<string>(this, text => _inserted.Add(text)));
        builder.CloseComponent();
    };

    private async Task<IRenderedComponent<ContainerFragment>> OpenAsync(bool withMetaverse = true)
    {
        var cut = Render(Menu(withMetaverse));
        await cut.Find("[data-testid='jim-insert-attribute-button']").ClickAsync(new MouseEventArgs());
        cut.WaitForElement("[data-testid='jim-insert-attribute-group-cs']");
        return cut;
    }

    private static IReadOnlyList<string> Items(IRenderedComponent<ContainerFragment> cut) =>
        cut.FindAll("[data-testid='jim-insert-attribute-item']").Select(e => e.TextContent.Trim()).ToList();

    [Test]
    public async Task InsertAttributeMenu_Open_ShowsBothGroupsNamedForTheirAccessorAsync()
    {
        var cut = await OpenAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find("[data-testid='jim-insert-attribute-group-mv']").TextContent.Trim(), Is.EqualTo("From the Metaverse · mv[...]"));
            Assert.That(cut.Find("[data-testid='jim-insert-attribute-group-cs']").TextContent.Trim(), Is.EqualTo("From HR · cs[...]"));
            Assert.That(cut.HasComponent<SearchField>(), Is.True, "the filter is the shared search box");
        }
    }

    [Test]
    public async Task InsertAttributeMenu_MetaverseAttributesASaveWouldRefuse_AreDisabledWithTheReasonAsync()
    {
        var cut = await OpenAsync();

        var buttons = cut.FindAll("button[data-insert-item]").ToDictionary(
            b => b.QuerySelector("[data-testid='jim-insert-attribute-item']")!.TextContent.Trim(),
            b => (Disabled: b.HasAttribute("disabled"), Title: b.GetAttribute("title")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buttons["Account Name mv[\"Account Name\"]"].Disabled, Is.False);
            Assert.That(buttons["Manager (Reference, not supported yet)"],
                Is.EqualTo((true, "Reference attributes cannot be read by a Derived Attribute Flow yet.")));
            Assert.That(buttons["Email (this flow's target)"],
                Is.EqualTo((true, "Reading Email here would make Email depend on itself.")));
            Assert.That(buttons["firstName cs[\"firstName\"]"].Disabled, Is.False);
        }
    }

    [Test]
    public async Task InsertAttributeMenu_ChoosingAnAttribute_ClosesTheMenuAsync()
    {
        var cut = await OpenAsync();

        await cut.FindAll("[data-testid='jim-insert-attribute-item']").First(e => e.TextContent.Contains("firstName")).ClickAsync(new MouseEventArgs());

        cut.WaitForState(() => cut.FindAll("[data-testid='jim-insert-attribute-group-cs']").Count == 0);
        Assert.That(_inserted, Is.EqualTo(new[] { "cs[\"firstName\"]" }));
    }

    [Test]
    public async Task InsertAttributeMenu_Escape_ClosesTheMenuWithoutInsertingAsync()
    {
        var cut = await OpenAsync();

        await cut.Find("[data-testid='jim-insert-attribute-menu']").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForState(() => cut.FindAll("[data-testid='jim-insert-attribute-group-cs']").Count == 0);
        Assert.That(_inserted, Is.Empty);
    }

    [Test]
    public async Task InsertAttributeMenu_CredentialAttribute_IsNotOfferedAsync()
    {
        var cut = await OpenAsync();

        Assert.That(Items(cut), Has.None.Contains("userPassword"), "credential attributes never travel through Attribute Flow");
    }

    [Test]
    public async Task InsertAttributeMenu_ChoosingAnAttribute_InsertsItsAccessorAsync()
    {
        var cut = await OpenAsync();

        await cut.FindAll("[data-testid='jim-insert-attribute-item']").First(e => e.TextContent.Contains("Account Name")).ClickAsync(new MouseEventArgs());

        Assert.That(_inserted, Is.EqualTo(new[] { "mv[\"Account Name\"]" }));
    }

    [Test]
    public async Task InsertAttributeMenu_NoMetaverseAttributes_OffersOnlyTheConnectedSystemGroupAsync()
    {
        var cut = await OpenAsync(withMetaverse: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid='jim-insert-attribute-group-mv']"), Is.Empty);
            Assert.That(Items(cut), Is.EqualTo(new[] { "firstName cs[\"firstName\"]" }));
        }
    }

    [Test]
    public async Task InsertAttributeMenu_Filter_NarrowsBothGroupsAsync()
    {
        var cut = await OpenAsync();

        var search = cut.FindComponent<SearchField>();
        await search.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("name"));

        cut.WaitForAssertion(() => Assert.That(Items(cut), Is.EqualTo(new[] { "Account Name mv[\"Account Name\"]", "firstName cs[\"firstName\"]" })));
    }
}
