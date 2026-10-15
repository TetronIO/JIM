// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Application.Interfaces;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Metaverse Attribute editor applies the Boolean rule (#2153): a Boolean attribute holds one value, so while
/// Boolean is the chosen data type the dialog offers no multi-valued plurality, and choosing Boolean resets a
/// multi-valued choice to single-valued.
/// </summary>
[TestFixture]
public class MetaverseAttributeEditorDialogTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        var metaverseRepository = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Metaverse).Returns(metaverseRepository.Object);
        metaverseRepository.Setup(r => r.GetMetaverseObjectTypesAsync(It.IsAny<bool>())).ReturnsAsync([]);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await DisposeComponentsAsync();
    }

    private IRenderedComponent<MudDialogProvider> OpenCreateDialog()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => dialogService.ShowAsync<MetaverseAttributeEditorDialog>("New Metaverse Attribute"));
        provider.WaitForState(() => provider.FindComponents<MudSelect<AttributePlurality>>().Count > 0);
        return provider;
    }

    // MudBlazor's MUD0012 analyzer forbids external reads of a two-way-bound parameter (Value/Disabled); reading a
    // component's own parameters is the bUnit pattern test/CLAUDE.md prescribes, so it is suppressed narrowly here.
#pragma warning disable MUD0012

    private static async Task ChooseTypeAsync(IRenderedComponent<MudDialogProvider> provider, AttributeDataType type)
    {
        var typeSelect = provider.FindComponent<MudSelect<AttributeDataType>>();
        await provider.InvokeAsync(() => typeSelect.Instance.ValueChanged.InvokeAsync(type));
    }

    private static async Task ChoosePluralityAsync(IRenderedComponent<MudDialogProvider> provider, AttributePlurality plurality)
    {
        var pluralitySelect = provider.FindComponent<MudSelect<AttributePlurality>>();
        await provider.InvokeAsync(() => pluralitySelect.Instance.ValueChanged.InvokeAsync(plurality));
    }

    private static bool MultiValuedOptionDisabled(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponent<MudSelect<AttributePlurality>>()
            .FindComponents<MudSelectItem<AttributePlurality>>()
            .Single(i => i.Instance.Value == AttributePlurality.MultiValued)
            .Instance.Disabled;

    [Test]
    public async Task ChoosingBoolean_AfterMultiValued_ResetsPluralityToSingleValuedAsync()
    {
        var provider = OpenCreateDialog();
        await ChoosePluralityAsync(provider, AttributePlurality.MultiValued);

        await ChooseTypeAsync(provider, AttributeDataType.Boolean);

        provider.WaitForAssertion(() =>
            Assert.That(provider.FindComponent<MudSelect<AttributePlurality>>().Instance.Value, Is.EqualTo(AttributePlurality.SingleValued)));
    }

    [Test]
    public async Task ChoosingBoolean_DisablesTheMultiValuedOptionAsync()
    {
        var provider = OpenCreateDialog();

        await ChooseTypeAsync(provider, AttributeDataType.Boolean);

        provider.WaitForAssertion(() => Assert.That(MultiValuedOptionDisabled(provider), Is.True));
    }

    [Test]
    public async Task ChoosingAnotherTypeAfterBoolean_ReEnablesTheMultiValuedOptionAsync()
    {
        var provider = OpenCreateDialog();
        await ChooseTypeAsync(provider, AttributeDataType.Boolean);

        await ChooseTypeAsync(provider, AttributeDataType.DateTime);

        provider.WaitForAssertion(() => Assert.That(MultiValuedOptionDisabled(provider), Is.False));
    }

    [Test]
    public void DefaultTextType_OffersTheMultiValuedOption()
    {
        var provider = OpenCreateDialog();

        Assert.That(MultiValuedOptionDisabled(provider), Is.False);
    }

    [Test]
    public async Task ChoosingBoolean_ExplainsWhyThePluralityIsFixedAsync()
    {
        var provider = OpenCreateDialog();
        Assert.That(provider.FindComponent<MudSelect<AttributePlurality>>().Instance.HelperText, Is.Null.Or.Empty);

        await ChooseTypeAsync(provider, AttributeDataType.Boolean);

        provider.WaitForAssertion(() =>
            Assert.That(provider.FindComponent<MudSelect<AttributePlurality>>().Instance.HelperText, Is.Not.Null.And.Not.Empty));
    }

#pragma warning restore MUD0012

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
