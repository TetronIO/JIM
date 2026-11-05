// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Web.Pages.Admin;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;

    /// <summary>
    /// The connection mode (#2021) lists Connected Systems and their Object Types through the application. Registered
    /// here rather than in <c>[SetUp]</c>: see <see cref="JimComponentTestContext.ConfigureAdditionalServices"/>.
    /// </summary>
    protected override void ConfigureAdditionalServices()
    {
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();

        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        _connectedSystems.Setup(r => r.GetConnectedSystemHeadersAsync()).ReturnsAsync(
        [
            new ConnectedSystemHeader { Id = 9, Name = "RESURGAM AD" },
            new ConnectedSystemHeader { Id = 8, Name = "PANOPLY AD" }
        ]);
        _connectedSystems.Setup(r => r.GetObjectTypesAsync(8)).ReturnsAsync(
        [
            new ConnectedSystemObjectType { Id = 80, Name = "user", ConnectedSystemId = 8, Selected = true },
            new ConnectedSystemObjectType { Id = 81, Name = "contact", ConnectedSystemId = 8, Selected = true },
            new ConnectedSystemObjectType { Id = 82, Name = "printer", ConnectedSystemId = 8, Selected = false }
        ]);
        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(new JimApplication(repository.Object)));
    }

    [Test]
    public async Task AddCriteria_ExpressionThatDoesNotParse_KeepsAddOffAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ImportRule(out _));

        ComposeExpression(provider, "Lower(cs[\"department\"]", SearchComparisonType.StartsWith, "fin");

        Assert.That(AddButton(provider).Instance.Disabled, Is.True);
    }

    [Test]
    public async Task AddCriteria_ExpressionReadingTheOtherSide_KeepsAddOffAndSaysWhyAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ImportRule(out _));

        ComposeExpression(provider, "Lower(mv[\"Department\"])", SearchComparisonType.StartsWith, "fin");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AddButton(provider).Instance.Disabled, Is.True);
            Assert.That(provider.Find("[data-testid='jim-scoping-criterion-problem']").TextContent, Does.Contain("mv[\"Department\"]"));
        }
    }

    [Test]
    public async Task AddCriteria_WorkableExpression_AddsAnExpressionCriterionComparedAsTextAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ImportRule(out var root));

        ComposeExpression(provider, DepartmentExpression, SearchComparisonType.StartsWith, "fin");
        var add = AddButton(provider);
        Assert.That(add.Instance.Disabled, Is.False);
        await add.Find("button").ClickAsync();

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
    public async Task AddCriteria_CompareAsChanged_ClearsAnOperatorTheNewTypeDoesNotOfferAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ImportRule(out _));
        ComposeExpression(provider, DepartmentExpression, SearchComparisonType.StartsWith, "fin");

        var compareAs = provider.FindComponents<MudSelect<AttributeDataType>>().Single();
        await provider.InvokeAsync(() => compareAs.Instance.ValueChanged.InvokeAsync(AttributeDataType.Number));

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
    public async Task AddCriteria_ExpressionAgainstARelativeDate_SaysItCostsAFullPassAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ImportRule(out _));
        ComposeExpression(provider, "ToDate(cs[\"leaveDate\"])", null, null);

        var compareAs = provider.FindComponents<MudSelect<AttributeDataType>>().Single();
        await provider.InvokeAsync(() => compareAs.Instance.ValueChanged.InvokeAsync(AttributeDataType.DateTime));
        var mode = provider.FindComponents<MudRadioGroup<DateCriteriaValueMode>>().Single();
        await provider.InvokeAsync(() => mode.Instance.ValueChanged.InvokeAsync(DateCriteriaValueMode.Relative));

        Assert.That(provider.Find("[data-testid='jim-scoping-criterion-full-pass']").TextContent,
            Does.Contain(SyncRuleScopingCriteria.FullReconciliationPassNotice));
    }

    [Test]
    public async Task AddCriteria_CompareAsMovedOffDateAfterChoosingARelativeDate_DropsTheRelativeDateAndItsNoticeAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ImportRule(out _));
        ComposeExpression(provider, DepartmentExpression, null, null);
        var compareAs = provider.FindComponents<MudSelect<AttributeDataType>>().Single();
        await provider.InvokeAsync(() => compareAs.Instance.ValueChanged.InvokeAsync(AttributeDataType.DateTime));
        var mode = provider.FindComponents<MudRadioGroup<DateCriteriaValueMode>>().Single();
        await provider.InvokeAsync(() => mode.Instance.ValueChanged.InvokeAsync(DateCriteriaValueMode.Relative));

        await provider.InvokeAsync(() => compareAs.Instance.ValueChanged.InvokeAsync(AttributeDataType.Text));

        Assert.That(provider.FindAll("[data-testid='jim-scoping-criterion-full-pass']"), Is.Empty);
    }

    [Test]
    public async Task AddCriteria_SwitchingBackToAttribute_DropsTheExpressionAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ImportRule(out _));
        ComposeExpression(provider, DepartmentExpression, null, null);

        var toggle = provider.FindComponent<SegmentedToggle<SyncRuleDetailScopingCriteriaGroup.CriterionKind>>();
        await provider.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(SyncRuleDetailScopingCriteriaGroup.CriterionKind.Attribute));

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

    [Test]
    public async Task AddCriteria_ImportRule_OffersNoConnectionAsync()
    {
        // A connection is a Metaverse Object's (#2021), so only an export rule can test one.
        var (provider, _) = await OpenAddDialogAsync(ImportRule(out _));

        var options = provider.FindComponent<SegmentedToggle<SyncRuleDetailScopingCriteriaGroup.CriterionKind>>().Instance.Options;

        Assert.That(options.Select(o => o.Value), Does.Not.Contain(SyncRuleDetailScopingCriteriaGroup.CriterionKind.Connection));
    }

    [Test]
    public async Task AddCriteria_Connection_AddsTheChosenObjectTypeWithHasNoAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ExportRule(out var root));
        await ChooseConnectionAsync(provider);

        var connectedSystem = provider.FindComponents<MudSelect<int?>>().First();
        await provider.InvokeAsync(() => connectedSystem.Instance.ValueChanged.InvokeAsync(8));
        Assert.That(AddButton(provider).Instance.Disabled, Is.True, "no Object Type is chosen yet");

        var objectType = provider.FindComponents<MudSelect<int?>>().Last();
        await provider.InvokeAsync(() => objectType.Instance.ValueChanged.InvokeAsync(81));
        var comparison = provider.FindComponents<MudSelect<SearchComparisonType?>>().Single();
        await provider.InvokeAsync(() => comparison.Instance.ValueChanged.InvokeAsync(SearchComparisonType.HasNoValue));

        var add = AddButton(provider);
        Assert.That(add.Instance.Disabled, Is.False);
        await add.Find("button").ClickAsync();

        var criterion = root.Criteria.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.ConnectedObjectTypeId, Is.EqualTo(81));
            Assert.That(criterion.ConnectedObjectType?.ConnectedSystem?.Name, Is.EqualTo("PANOPLY AD"), "named for the list until it is saved");
            Assert.That(criterion.ComparisonType, Is.EqualTo(SearchComparisonType.HasNoValue));
            Assert.That(criterion.MetaverseAttribute, Is.Null);
            Assert.That(criterion.IsValid(), Is.True);
        }
    }

    [Test]
    public async Task AddCriteria_ConnectionsObjectTypes_OfferOnlyThoseTheSystemSelectsAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ExportRule(out _));
        await ChooseConnectionAsync(provider);

        var connectedSystem = provider.FindComponents<MudSelect<int?>>().First();
        await provider.InvokeAsync(() => connectedSystem.Instance.ValueChanged.InvokeAsync(8));
        var objectTypes = provider.FindComponents<MudSelect<int?>>().Last();
        await provider.InvokeAsync(() => objectTypes.Instance.OpenMenu());

        var offered = provider.FindComponents<MudSelectItem<int?>>().Select(i => i.Instance.Value).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(offered, Does.Contain((int?)80).And.Contain((int?)81));
            Assert.That(offered, Does.Not.Contain((int?)82), "an Object Type the system does not select is never imported, so never joined");
        }
    }

    [Test]
    public async Task AddCriteria_Connection_OffersOnlyHasAndHasNoAsync()
    {
        var (provider, _) = await OpenAddDialogAsync(ExportRule(out _));
        await ChooseConnectionAsync(provider);

        var comparison = provider.FindComponents<MudSelect<SearchComparisonType?>>().Single();
        await provider.InvokeAsync(() => comparison.Instance.OpenMenu());

        Assert.That(provider.FindComponents<MudSelectItem<SearchComparisonType?>>().Select(i => i.Instance.Value),
            Is.EquivalentTo(new SearchComparisonType?[] { SearchComparisonType.HasValue, SearchComparisonType.HasNoValue }));
    }

    [Test]
    public void CriteriaList_ConnectionCriterion_NamesTheObjectTypeAndItsConnectedSystem()
    {
        var syncRule = ExportRule(out var root);
        root.Criteria.Add(new SyncRuleScopingCriteria
        {
            ConnectedObjectTypeId = 80,
            ConnectedObjectType = new ConnectedSystemObjectType { Id = 80, Name = "user", ConnectedSystemId = 8, ConnectedSystem = new ConnectedSystem { Id = 8, Name = "PANOPLY AD" } },
            ComparisonType = SearchComparisonType.HasValue
        });

        var group = Render<SyncRuleDetailScopingCriteriaGroup>(p => p
            .Add(c => c.SyncRule, syncRule)
            .Add(c => c.SyncRuleScopingCriteriaGroup, root));

        var chip = group.FindComponents<ObjectChip>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.HasComponent<AttributeChip>(), Is.False, "a connection is not an attribute");
            Assert.That(group.Find("[data-testid='jim-scoping-criterion-connection-type']").TextContent, Is.EqualTo("user"));
            Assert.That(chip.Instance.Kind, Is.EqualTo(ObjectChipKind.ConnectedSystem));
            Assert.That(chip.Instance.Name, Is.EqualTo("PANOPLY AD"));
        }
    }

    private static async Task ChooseConnectionAsync(IRenderedComponent<MudDialogProvider> provider)
    {
        var toggle = provider.FindComponent<SegmentedToggle<SyncRuleDetailScopingCriteriaGroup.CriterionKind>>();
        await provider.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(SyncRuleDetailScopingCriteriaGroup.CriterionKind.Connection));
    }

    private static SyncRule ExportRule(out SyncRuleScopingCriteriaGroup root)
    {
        root = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        return new SyncRule
        {
            Id = 41,
            Name = "GAL: PANOPLY people into RESURGAM",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemId = 9,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 90, Name = "contact", Attributes = [] },
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", Attributes = [] },
            ObjectScopingCriteriaGroups = [root]
        };
    }

    private sealed class FakeJimApplicationFactory(JimApplication jimApplication) : IJimApplicationFactory
    {
        public JimApplication Create() => jimApplication;
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleDetailScopingCriteriaGroup> Group)> OpenAddDialogAsync(SyncRule syncRule)
    {
        var provider = Render<MudDialogProvider>();
        Render<MudPopoverProvider>();
        var root = syncRule.ObjectScopingCriteriaGroups.Single();
        var group = Render<SyncRuleDetailScopingCriteriaGroup>(p => p
            .Add(c => c.SyncRule, syncRule)
            .Add(c => c.SyncRuleScopingCriteriaGroup, root));
        await group.FindComponents<MudButton>().Single(b => b.Markup.Contains(">Add Criteria<")).Find("button").ClickAsync();
        provider.WaitForAssertion(() => Assert.That(provider.FindComponents<MudButton>().Any(b => b.Markup.Contains("Add Criteria")), Is.True));
        return (provider, group);
    }

    private static void ComposeExpression(IRenderedComponent<MudDialogProvider> provider, string expression,
        SearchComparisonType? comparison, string? value)
    {
        var toggle = provider.FindComponent<SegmentedToggle<SyncRuleDetailScopingCriteriaGroup.CriterionKind>>();
        provider.InvokeAsync(() => toggle.Instance.ValueChanged.InvokeAsync(SyncRuleDetailScopingCriteriaGroup.CriterionKind.Expression));

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
