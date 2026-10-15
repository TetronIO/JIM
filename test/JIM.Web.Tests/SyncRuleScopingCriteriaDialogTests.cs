// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Web.Pages.Admin;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Add Criteria dialog's expression mode (#2054): a Scoping Criterion can compare the value an expression computes,
/// as a type the administrator chooses. Add waits until the criterion is one the save would accept, and the criterion
/// list shows the expression a criterion compares.
/// </summary>
[TestFixture]
public class SyncRuleScopingCriteriaDialogTests : JimComponentTestContext
{
    private const string DepartmentExpression = "Lower(Trim(cs[\"department\"]))";

    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
    }

    [Test]
    public void AddCriteria_ExpressionThatDoesNotParse_KeepsAddOff()
    {
        var (provider, _) = OpenAddDialog(ImportRule(out _));

        ComposeExpression(provider, "Lower(cs[\"department\"]", SearchComparisonType.StartsWith, "fin");

        Assert.That(AddButton(provider).Instance.Disabled, Is.True);
    }

    [Test]
    public void AddCriteria_ExpressionReadingTheOtherSide_KeepsAddOffAndSaysWhy()
    {
        var (provider, _) = OpenAddDialog(ImportRule(out _));

        ComposeExpression(provider, "Lower(mv[\"Department\"])", SearchComparisonType.StartsWith, "fin");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AddButton(provider).Instance.Disabled, Is.True);
            Assert.That(provider.Find("[data-testid='jim-scoping-criterion-problem']").TextContent, Does.Contain("mv[\"Department\"]"));
        }
    }

    [Test]
    public void AddCriteria_WorkableExpression_AddsAnExpressionCriterionComparedAsText()
    {
        var (provider, _) = OpenAddDialog(ImportRule(out var root));

        ComposeExpression(provider, DepartmentExpression, SearchComparisonType.StartsWith, "fin");
        var add = AddButton(provider);
        Assert.That(add.Instance.Disabled, Is.False);
        provider.InvokeAsync(() => add.Find("button").Click());

        var criterion = root.Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.Expression, Is.EqualTo(DepartmentExpression));
            Assert.That(criterion.CompareAs, Is.EqualTo(AttributeDataType.Text), "Compare As defaults to Text");
            Assert.That(criterion.ConnectedSystemAttribute, Is.Null);
            Assert.That(criterion.ComparisonType, Is.EqualTo(SearchComparisonType.StartsWith));
            Assert.That(criterion.StringValue, Is.EqualTo("fin"));
            Assert.That(criterion.IsValid(), Is.True);
        }
    }

    [Test]
    public void AddCriteria_CompareAsChanged_ClearsAnOperatorTheNewTypeDoesNotOffer()
    {
        var (provider, _) = OpenAddDialog(ImportRule(out _));
        ComposeExpression(provider, DepartmentExpression, SearchComparisonType.StartsWith, "fin");

        var compareAs = provider.FindComponents<MudSelect<AttributeDataType>>().Single();
        provider.InvokeAsync(() => compareAs.Instance.ValueChanged.InvokeAsync(AttributeDataType.Number));

        // MudSelect keeps Value as a plain parameter (no GetState accessor), so the analyser's rule does not apply.
#pragma warning disable MUD0012
        var chosenOperator = provider.FindComponents<MudSelect<SearchComparisonType?>>().Single().Instance.Value;
#pragma warning restore MUD0012
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chosenOperator, Is.Null, "Starts with is not a Number operator");
            Assert.That(AddButton(provider).Instance.Disabled, Is.True);
        }
    }

    [Test]
    public void AddCriteria_ExpressionAgainstARelativeDate_SaysItCostsAFullPass()
    {
        var (provider, _) = OpenAddDialog(ImportRule(out _));
        ComposeExpression(provider, "ToDate(cs[\"leaveDate\"])", null, null);

        var compareAs = provider.FindComponents<MudSelect<AttributeDataType>>().Single();
        provider.InvokeAsync(() => compareAs.Instance.ValueChanged.InvokeAsync(AttributeDataType.DateTime));
        var mode = provider.FindComponents<MudRadioGroup<DateCriteriaValueMode>>().Single();
        provider.InvokeAsync(() => mode.Instance.ValueChanged.InvokeAsync(DateCriteriaValueMode.Relative));

        Assert.That(provider.Find("[data-testid='jim-scoping-criterion-full-pass']").TextContent,
            Does.Contain(SyncRuleScopingCriteria.FullReconciliationPassNotice));
    }

    [Test]
    public void AddCriteria_SwitchingBackToAttribute_DropsTheExpression()
    {
        var (provider, _) = OpenAddDialog(ImportRule(out _));
        ComposeExpression(provider, DepartmentExpression, null, null);

        var toggle = provider.FindComponent<SegmentedToggle<bool>>();
        provider.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.HasComponent<ExpressionEditor>(), Is.False);
            Assert.That(provider.FindComponents<MudSelect<ConnectedSystemObjectTypeAttribute>>(), Has.Count.EqualTo(1));
            Assert.That(AddButton(provider).Instance.Disabled, Is.True, "no attribute is chosen yet");
        }
    }

    [Test]
    public void CriteriaList_ExpressionCriterion_ShowsAnExpressionChip()
    {
        var syncRule = ImportRule(out var root);
        root.Criteria.Add(new SyncRuleScopingCriteria
        {
            Expression = DepartmentExpression,
            CompareAs = AttributeDataType.Text,
            ComparisonType = SearchComparisonType.StartsWith,
            StringValue = "fin"
        });

        var group = Render<SyncRuleDetailScopingCriteriaGroup>(p => p
            .Add(c => c.SyncRule, syncRule)
            .Add(c => c.SyncRuleScopingCriteriaGroup, root));

        var chip = group.FindComponents<AttributeChip>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Instance.Kind, Is.EqualTo(AttributeChipKind.Expression));
            Assert.That(chip.Instance.Expression, Is.EqualTo(DepartmentExpression));
        }
    }

    [Test]
    public void CriteriaList_AttributeCriterion_ShowsAConnectedSystemChip()
    {
        var syncRule = ImportRule(out var root);
        root.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = syncRule.ConnectedSystemObjectType!.Attributes.First(),
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Finance"
        });

        var group = Render<SyncRuleDetailScopingCriteriaGroup>(p => p
            .Add(c => c.SyncRule, syncRule)
            .Add(c => c.SyncRuleScopingCriteriaGroup, root));

        var chip = group.FindComponents<AttributeChip>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chip.Instance.Kind, Is.EqualTo(AttributeChipKind.ConnectedSystem));
            Assert.That(chip.Instance.Name, Is.EqualTo("department"));
        }
    }

    private (IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleDetailScopingCriteriaGroup> Group) OpenAddDialog(SyncRule syncRule)
    {
        var provider = Render<MudDialogProvider>();
        Render<MudPopoverProvider>();
        var root = syncRule.ObjectScopingCriteriaGroups.Single();
        var group = Render<SyncRuleDetailScopingCriteriaGroup>(p => p
            .Add(c => c.SyncRule, syncRule)
            .Add(c => c.SyncRuleScopingCriteriaGroup, root));
        group.FindComponents<MudButton>().Single(b => b.Markup.Contains(">Add Criteria<")).Find("button").Click();
        provider.WaitForAssertion(() => Assert.That(provider.FindComponents<MudButton>().Any(b => b.Markup.Contains("Add Criteria")), Is.True));
        return (provider, group);
    }

    private static void ComposeExpression(IRenderedComponent<MudDialogProvider> provider, string expression,
        SearchComparisonType? comparison, string? value)
    {
        var toggle = provider.FindComponent<SegmentedToggle<bool>>();
        provider.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(true));

        var expressionEditor = provider.FindComponent<ExpressionEditor>();
        provider.InvokeAsync(() => expressionEditor.Instance.ValueChanged.InvokeAsync(expression));

        if (comparison is { } op)
        {
            var select = provider.FindComponents<MudSelect<SearchComparisonType?>>().Single();
            provider.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(op));
        }

        if (value != null)
        {
            var valueField = provider.FindComponent<CriterionValueEditor>().FindComponents<MudTextField<string>>().First();
            provider.InvokeAsync(() => valueField.Instance.ValueChanged.InvokeAsync(value));
        }
    }

    private static IRenderedComponent<MudButton> AddButton(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponents<MudButton>().Single(b => b.Markup.Contains("Add Criteria"));

    private static SyncRule ImportRule(out SyncRuleScopingCriteriaGroup root)
    {
        root = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        return new SyncRule
        {
            Id = 40,
            Name = "Import users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 3,
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "department", Type = AttributeDataType.Text, Selected = true },
                    new ConnectedSystemObjectTypeAttribute { Id = 102, Name = "leaveDate", Type = AttributeDataType.Text, Selected = true }
                ]
            },
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", Attributes = [] },
            ObjectScopingCriteriaGroups = [root]
        };
    }
}
