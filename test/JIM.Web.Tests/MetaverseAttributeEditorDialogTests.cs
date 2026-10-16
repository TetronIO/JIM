// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using MudBlazor.Extensions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Metaverse Attribute editor's plurality rules (#2153). The Boolean rule (S2): a Boolean attribute holds one
/// value, so while Boolean is the chosen data type the dialog offers no multi-valued plurality, and choosing Boolean
/// resets a multi-valued choice to single-valued. The schema locks (S3, D3): While Metaverse Objects hold values for the attribute,
/// the only schema change JIM allows is widening it from single-valued to multi-valued with its type unchanged, so the
/// dialog unlocks exactly that: Plurality on a single-valued attribute. Data Type stays locked, as does Plurality on an
/// attribute that is already multi-valued, and each locked control says why.
/// </summary>
[TestFixture]
public class MetaverseAttributeEditorDialogTests : JimComponentTestContext
{
    private Mock<IMetaverseRepository> _metaverseRepository = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _metaverseRepository = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Metaverse).Returns(_metaverseRepository.Object);
        _metaverseRepository.Setup(r => r.GetMetaverseObjectTypesAsync(It.IsAny<bool>())).ReturnsAsync([]);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private IRenderedComponent<MudDialogProvider> OpenEditor(AttributeDataType type, AttributePlurality plurality, int objectsWithValues, bool readOnly = false)
    {
        var attribute = new MetaverseAttribute { Id = 42, Name = "costCentre", Type = type, AttributePlurality = plurality };
        _metaverseRepository.Setup(r => r.GetAttributeValueObjectCountAsync(42)).ReturnsAsync(objectsWithValues);

        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => MetaverseAttributeEditorDialog.ShowAsync(dialogService, attribute, readOnly));
        provider.WaitForState(() => provider.FindComponents<MudSelect<AttributePlurality>>().Count > 0);
        return provider;
    }

    private IRenderedComponent<MudDialogProvider> OpenCreateDialog()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => dialogService.ShowAsync<MetaverseAttributeEditorDialog>("New Metaverse Attribute"));
        provider.WaitForState(() => provider.FindComponents<MudSelect<AttributePlurality>>().Count > 0);
        return provider;
    }

    private static MudSelect<AttributeDataType> TypeSelect(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponent<MudSelect<AttributeDataType>>().Instance;

    private static MudSelect<AttributePlurality> PluralitySelect(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponent<MudSelect<AttributePlurality>>().Instance;

    [Test]
    public void SingleValuedAttributeWithValues_UnlocksPluralityOnlyAndExplainsTheTypeLock()
    {
        var provider = OpenEditor(AttributeDataType.Text, AttributePlurality.SingleValued, objectsWithValues: 12);

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(TypeSelect(provider).Disabled, Is.True, "a type change would invalidate the stored values");
                Assert.That(TypeSelect(provider).HelperText, Is.Not.Null.And.Not.Empty);
                Assert.That(PluralitySelect(provider).Disabled, Is.False, "widening to multi-valued keeps every stored value");
                Assert.That(PluralitySelect(provider).HelperText, Is.Not.Null.And.Not.Empty);
            }
        });
    }

    [Test]
    public void MultiValuedAttributeWithValues_LocksBothAndExplainsWhy()
    {
        var provider = OpenEditor(AttributeDataType.Text, AttributePlurality.MultiValued, objectsWithValues: 12);

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(TypeSelect(provider).Disabled, Is.True);
                Assert.That(PluralitySelect(provider).Disabled, Is.True, "multi-valued to single-valued could lose values");
                Assert.That(TypeSelect(provider).HelperText, Is.Not.Null.And.Not.Empty);
                Assert.That(PluralitySelect(provider).HelperText, Is.Not.Null.And.Not.Empty);
            }
        });
    }

    [TestCase(AttributePlurality.SingleValued)]
    [TestCase(AttributePlurality.MultiValued)]
    public void AttributeWithNoValues_LeavesBothUnlocked(AttributePlurality plurality)
    {
        var provider = OpenEditor(AttributeDataType.Text, plurality, objectsWithValues: 0);

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(TypeSelect(provider).Disabled, Is.False);
                Assert.That(PluralitySelect(provider).Disabled, Is.False);
                Assert.That(TypeSelect(provider).HelperText, Is.Null.Or.Empty, "nothing is locked, so nothing needs explaining");
                Assert.That(PluralitySelect(provider).HelperText, Is.Null.Or.Empty);
            }
        });
    }

    [Test]
    public void SingleValuedAttributeWithValues_ChoosingMultiValued_KeepsTheTypeLocked()
    {
        var provider = OpenEditor(AttributeDataType.Text, AttributePlurality.SingleValued, objectsWithValues: 12);
        provider.WaitForAssertion(() => Assert.That(PluralitySelect(provider).Disabled, Is.False));

        var plurality = provider.FindComponent<MudSelect<AttributePlurality>>();
        provider.InvokeAsync(() => plurality.Instance.ValueChanged.InvokeAsync(AttributePlurality.MultiValued));

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(PluralitySelect(provider).GetState(x => x.Value), Is.EqualTo(AttributePlurality.MultiValued));
                Assert.That(PluralitySelect(provider).Disabled, Is.False, "the administrator can still change their mind");
                Assert.That(TypeSelect(provider).Disabled, Is.True, "widening keeps the type, so the type stays locked");
            }
        });
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

    [Test]
    public void SingleValuedBooleanWithValues_LocksPluralityBecauseABooleanCannotBeWidened()
    {
        var provider = OpenEditor(AttributeDataType.Boolean, AttributePlurality.SingleValued, objectsWithValues: 12);

        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(PluralitySelect(provider).Disabled, Is.True, "a Boolean holds one value, so there is nothing to widen to");
                Assert.That(PluralitySelect(provider).HelperText, Is.Not.Null.And.Not.Empty);
            }
        });
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
