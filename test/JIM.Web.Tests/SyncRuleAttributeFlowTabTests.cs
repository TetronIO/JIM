// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Synchronisation Rule editor's Attribute Flow tab, and specifically the pickers in its add/edit dialog. A
/// picker's options are rich (a "Suggested" chip sits beside attributes a Standard Mapping recommends), and
/// MudSelect paints the chosen option, chip and all, inside the input unless it is given a text label for the
/// value. The Metaverse Attribute pickers have always had one; the Connected System pickers did not, and a 32px
/// chip inside a 56px outlined field sat on the field's bottom border.
/// </summary>
[TestFixture]
public class SyncRuleAttributeFlowTabTests : JimComponentTestContext
{
    private JimApplication _jim = null!;

    protected override void ConfigureAdditionalServices()
    {
        // Neither loader the dialog runs on opening reaches the application layer for the rule built here: the
        // Standard Mapping hints need a Connected System id, and the contributor counts an import rule. The
        // factory still has to be resolvable for the component to construct.
        _jim = new JimApplication(new Mock<IRepository>().Object);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(_jim));
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [Test]
    public void SyncRuleAttributeFlowTab_ExportTargetPicker_RendersTheChosenAttributeAsTextRatherThanItsOption()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 5,
            Name = "company",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };

        var picker = OpenAddDialogAndFindPicker(SyncRuleDirection.Export, attribute, "Connected System Attribute", "Attribute");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(picker.ToStringFunc, Is.Not.Null, "the chosen attribute must render as text, not as its option");
            Assert.That(picker.ToStringFunc!(attribute), Is.EqualTo("company (Text : Single Valued)"));
        }
    }

    [Test]
    public void SyncRuleAttributeFlowTab_ImportSourcePicker_RendersTheChosenAttributeAsTextRatherThanItsOption()
    {
        // The import-side sibling. Its options carry no chip today, but it is the same picker over the same
        // attributes and its label should read the same as the export target's and the Metaverse pickers'.
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 6,
            Name = "memberOf",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };

        var picker = OpenAddDialogAndFindPicker(SyncRuleDirection.Import, attribute, "Connected System Attribute", "Attribute");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(picker.ToStringFunc, Is.Not.Null, "the chosen attribute must render as text, not as its option");
            Assert.That(picker.ToStringFunc!(attribute), Is.EqualTo("memberOf (Reference : Multi Valued)"));
        }
    }

    /// <summary>
    /// Opens the add dialog on a rule of the given direction whose Connected System Object Type carries the one
    /// attribute, chooses the "Attribute" source type (the source picker renders only once one is chosen), on an
    /// export rule also chooses the Metaverse source attribute (the target section renders only once the source
    /// is settled), and returns the named Connected System attribute picker. The dialog is inline, so its content
    /// renders inside the dialog provider rather than inside the tab; the provider is what is searched, and the
    /// tab is only clicked.
    /// </summary>
    private MudSelect<ConnectedSystemObjectTypeAttribute> OpenAddDialogAndFindPicker(
        SyncRuleDirection direction, ConnectedSystemObjectTypeAttribute attribute, string label, string sourceType)
    {
        var sourceAttribute = new MetaverseAttribute
        {
            Id = 10,
            Name = "Company",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };

        var provider = Render<MudDialogProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, BuildRule(direction, attribute, sourceAttribute)));

        tab.FindAll("button").First(b => b.TextContent.Contains("Add Attribute Flow")).Click();

        provider.WaitForAssertion(() => Assert.That(provider.HasComponent<MudSelect<string>>(), Is.True));
        var sourceTypePicker = provider.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Source Type");
        provider.InvokeAsync(() => sourceTypePicker.Instance.ValueChanged.InvokeAsync(sourceType)).GetAwaiter().GetResult();

        if (direction == SyncRuleDirection.Export)
        {
            provider.WaitForAssertion(() => Assert.That(provider.HasComponent<MudSelect<MetaverseAttribute>>(), Is.True));
            var sourcePicker = provider.FindComponents<MudSelect<MetaverseAttribute>>().Single(s => s.Instance.Label == "Metaverse Attribute");
            provider.InvokeAsync(() => sourcePicker.Instance.ValueChanged.InvokeAsync(sourceAttribute)).GetAwaiter().GetResult();
        }

        provider.WaitForAssertion(() => Assert.That(FindPicker(provider, label), Is.Not.Null));
        return FindPicker(provider, label)!.Instance;
    }

    private static IRenderedComponent<MudSelect<ConnectedSystemObjectTypeAttribute>>? FindPicker(
        IRenderedComponent<MudDialogProvider> provider, string label) =>
        provider.FindComponents<MudSelect<ConnectedSystemObjectTypeAttribute>>().SingleOrDefault(s => s.Instance.Label == label);

    private static SyncRule BuildRule(
        SyncRuleDirection direction, ConnectedSystemObjectTypeAttribute attribute, MetaverseAttribute sourceAttribute)
    {
        var connectedSystemObjectType = new ConnectedSystemObjectType { Id = 2, Name = "user" };
        connectedSystemObjectType.Attributes.Add(attribute);

        var metaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User" };
        metaverseObjectType.Attributes.Add(sourceAttribute);

        return new SyncRule
        {
            Id = 4,
            Name = "Cross-Domain Export Users",
            Direction = direction,
            MetaverseObjectType = metaverseObjectType,
            ConnectedSystemObjectType = connectedSystemObjectType
        };
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }
}
