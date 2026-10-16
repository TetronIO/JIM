// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// A Synchronisation Rule's own Object Matching Rules, in advanced matching mode (#2052): an expression rule can be
/// composed and added, the Add button waits until the rule is one the save would accept, and the rule cards show
/// what each rule compares whichever kind of source it has.
/// </summary>
[TestFixture]
public class SyncRuleMatchingTabTests : JimComponentTestContext
{
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new Mock<IRepository>().Object));
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
    }

    [Test]
    public void AddDialog_UnworkableExpression_KeepsAddMatchingRuleOff()
    {
        var syncRule = AdvancedModeImportRule();
        var (provider, _) = OpenAddDialog(syncRule);

        ComposeExpressionRule(provider, "Trim(mv[\"Department\"])");

        Assert.That(AddButton(provider).Instance.Disabled, Is.True);
    }

    [Test]
    public void AddDialog_WorkableExpression_AddsAnExpressionRuleToTheSynchronisationRule()
    {
        var syncRule = AdvancedModeImportRule();
        var (provider, tab) = OpenAddDialog(syncRule);

        ComposeExpressionRule(provider, "\"E\" + Trim(cs[\"employeeId\"])");
        var add = AddButton(provider);
        Assert.That(add.Instance.Disabled, Is.False);
        provider.InvokeAsync(() => add.Find("button").Click());

        var rule = syncRule.ObjectMatchingRules.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rule.Sources.Single().Expression, Is.EqualTo("\"E\" + Trim(cs[\"employeeId\"])"));
            Assert.That(rule.Sources.Single().ConnectedSystemAttributeId, Is.Null);
            Assert.That(rule.TargetMetaverseAttribute!.Name, Is.EqualTo("Employee ID"));
            Assert.That(rule.MetaverseObjectTypeId, Is.Null, "an advanced mode rule takes its type from the Synchronisation Rule");
        }

        tab.Render();
        Assert.That(tab.FindComponents<AttributeChip>().Any(c => c.Instance.Kind == AttributeChipKind.Expression), Is.True,
            "the card shows the expression the rule compares");
    }

    [Test]
    public void AddDialog_OffersMultiValuedMetaverseAttributes()
    {
        // Every value is looked up now, so the single-valued restriction advanced mode carried is gone.
        var syncRule = AdvancedModeImportRule();
        var (provider, _) = OpenAddDialog(syncRule);

        var targets = provider.FindComponents<MudSelect<MetaverseAttribute>>().Single()
            .FindComponents<MudSelectItem<MetaverseAttribute>>().Select(i => i.Instance.Value!.Name);

        Assert.That(targets, Does.Contain("Proxy Addresses"));
    }

    private (IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleMatchingTab> Tab) OpenAddDialog(SyncRule syncRule)
    {
        var provider = Render<MudDialogProvider>();
        var tab = Render<SyncRuleMatchingTab>(p => p.Add(c => c.SyncRule, syncRule));
        tab.FindComponents<MudButton>().Single(b => b.Markup.Contains("Add Matching")).Find("button").Click();
        provider.WaitForAssertion(() => Assert.That(provider.HasComponent<ObjectMatchingRuleEditor>(), Is.True));
        return (provider, tab);
    }

    private static void ComposeExpressionRule(IRenderedComponent<MudDialogProvider> provider, string expression)
    {
        var editor = provider.FindComponent<ObjectMatchingRuleEditor>();
        var toggle = editor.FindComponent<SegmentedToggle<ObjectMatchingRuleSourceKind>>();
        provider.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(ObjectMatchingRuleSourceKind.Expression));

        var expressionEditor = editor.FindComponent<ExpressionEditor>();
        provider.InvokeAsync(() => expressionEditor.Instance.ValueChanged.InvokeAsync(expression));

        var target = editor.FindComponents<MudSelect<MetaverseAttribute>>().Single();
        var employeeId = target.FindComponents<MudSelectItem<MetaverseAttribute>>().Single(i => i.Instance.Value!.Name == "Employee ID").Instance.Value;
        provider.InvokeAsync(() => target.Instance.ValueChanged.InvokeAsync(employeeId));
    }

    private static IRenderedComponent<MudButton> AddButton(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponents<MudButton>().Single(b => b.Markup.Contains("Add Matching Rule"));

    private static SyncRule AdvancedModeImportRule()
    {
        var connectedSystem = new ConnectedSystem { Id = 3, Name = "Directory", ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
        return new SyncRule
        {
            Id = 40,
            Name = "Import users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystem = connectedSystem,
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "employeeId", Type = AttributeDataType.Text, Selected = true }]
            },
            MetaverseObjectType = new MetaverseObjectType
            {
                Id = 1,
                Name = "Person",
                Attributes =
                [
                    new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = AttributeDataType.Text },
                    new MetaverseAttribute { Id = 202, Name = "Proxy Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued }
                ]
            }
        };
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}
