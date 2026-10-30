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
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.TestSupport;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Attribute Flow tab's Metaverse-Derived Attribute Flow affordances (#1750, Phase 6, mockups A to D), against a
/// real <see cref="JimApplication"/> over mocked repositories so the analysis and the dependant checks are the
/// Application layer's own: the Derived chip, the Insert attribute menu, the live analysis in the dialog (the panel and
/// the loop that disables Update), and the confirmation before removing a flow derived flows read.
/// </summary>
/// <remarks>
/// The rule is the runtime scenario's: HR Inbound flows Account Name from the directory, derives Email from Account
/// Name and User Principal Name from Email.
/// </remarks>
[TestFixture]
public class SyncRuleAttributeFlowTabDerivedFlowTests : JimComponentTestContext
{
    private const int UserTypeId = 1;
    private readonly FreshJimApplicationFactory _factory = new();
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IMetaverseRepository> _mvRepo = null!;
    private Mock<ISyncRepository> _syncRepo = null!;

    private static readonly MetaverseAttribute AccountName = new() { Id = 10, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private static readonly MetaverseAttribute Email = new() { Id = 11, Name = "Email", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private static readonly MetaverseAttribute UserPrincipalName = new() { Id = 12, Name = "User Principal Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private static readonly MetaverseAttribute Department = new() { Id = 13, Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private static readonly MetaverseObjectType User = new() { Id = UserTypeId, Name = "User", PluralName = "Users", Attributes = [AccountName, Email, UserPrincipalName, Department] };

    protected override void ConfigureAdditionalServices()
    {
        _csRepo = new Mock<IConnectedSystemRepository>();
        _mvRepo = new Mock<IMetaverseRepository>();
        // A generated flow's row reads its retired values count (#242), so a rule holding one needs the sync repository.
        _syncRepo = new Mock<ISyncRepository>();
        _syncRepo.Setup(r => r.GetRetiredGeneratedValueCountsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new List<RetiredGeneratedValueCount>());
        _factory.BuildSyncRepository = () => _syncRepo.Object;
        _factory.BuildRepository = () =>
        {
            var repo = new Mock<IRepository>();
            repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
            repo.Setup(r => r.Metaverse).Returns(_mvRepo.Object);
            repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
            return repo.Object;
        };
        Services.AddSingleton<IJimApplicationFactory>(_factory);
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
    }

    [SetUp]
    public void SetUp()
    {
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(UserTypeId)).ReturnsAsync(() => [BuildRule()]);
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.GetConnectedSystemNamesAsync()).ReturnsAsync(new Dictionary<int, string> { [5] = "HR" });
        _mvRepo.Setup(r => r.GetMetaverseObjectTypeAsync(UserTypeId, true)).ReturnsAsync(User);
        _mvRepo.Setup(r => r.GetContributedValuesSummaryAsync(It.IsAny<int>(), It.IsAny<int?>())).ReturnsAsync(new ContributedValuesSummary());
        // The dialog's Standard Mapping hints: none.
        _mvRepo.Setup(r => r.GetStandardMappingsForObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(new List<MetaverseAttributeStandardMapping>());
    }

    // ─── scaffolding ───

    /// <summary>
    /// HR Inbound as persisted (and, as a fresh instance per call, as the page holds it).
    /// </summary>
    private static SyncRule BuildRule(SyncRuleDirection direction = SyncRuleDirection.Import)
    {
        var connectedSystemObjectType = new ConnectedSystemObjectType { Id = 2, Name = "person" };
        connectedSystemObjectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 20, Name = "firstName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued });

        var rule = new SyncRule
        {
            Id = 12,
            Name = "HR Inbound",
            Direction = direction,
            Enabled = true,
            ConnectedSystemId = 5,
            ConnectedSystem = new ConnectedSystem { Name = "HR" },
            ConnectedSystemObjectType = connectedSystemObjectType,
            MetaverseObjectType = User,
            MetaverseObjectTypeId = UserTypeId
        };
        AddExpression(rule, 1, AccountName, "cs[\"firstName\"]");
        AddExpression(rule, 2, Email, "mv[\"Account Name\"] + \"@panoply.local\"");
        AddExpression(rule, 3, UserPrincipalName, "mv[\"Email\"]");
        return rule;
    }

    /// <summary>
    /// An export rule writing the directory's mail from the Metaverse's Email through an Expression.
    /// </summary>
    private static SyncRule BuildExportRule()
    {
        var mail = new ConnectedSystemObjectTypeAttribute { Id = 21, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var connectedSystemObjectType = new ConnectedSystemObjectType { Id = 2, Name = "person" };
        connectedSystemObjectType.Attributes.Add(mail);

        var rule = new SyncRule
        {
            Id = 13,
            Name = "HR Outbound",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemId = 5,
            ConnectedSystem = new ConnectedSystem { Name = "HR" },
            ConnectedSystemObjectType = connectedSystemObjectType,
            MetaverseObjectType = User,
            MetaverseObjectTypeId = UserTypeId
        };
        var mapping = new SyncRuleMapping
        {
            Id = 4,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = mail,
            TargetConnectedSystemAttributeId = mail.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "mv[\"Email\"]" });
        rule.AttributeFlowRules.Add(mapping);
        return rule;
    }

    private static void AddExpression(SyncRule rule, int id, MetaverseAttribute target, string expression)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        rule.AttributeFlowRules.Add(mapping);
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleAttributeFlowTab> Tab)> RenderCardsAsync(SyncRule rule)
    {
        var provider = Render<MudDialogProvider>();
        Render<MudPopoverProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, rule));
        // The card view draws every flow; the table is a virtualised grid that draws rows from the browser's
        // measurements, which bUnit does not have.
        await tab.FindComponents<MudIconButton>().Single(b => b.Instance.Icon == Icons.Material.Filled.GridView).Find("button").ClickAsync();
        tab.WaitForState(() => tab.FindAll("button[aria-label='edit']").Count == rule.AttributeFlowRules.Count);
        return (provider, tab);
    }

    private static Task OpenEditAsync(IRenderedComponent<SyncRuleAttributeFlowTab> tab, int mappingIndex) => PressAsync(tab, "edit", mappingIndex);

    private static async Task PressAsync(IRenderedComponent<SyncRuleAttributeFlowTab> tab, string action, int mappingIndex) =>
        await tab.FindAll($"button[aria-label='{action}']")[mappingIndex].ClickAsync();

    private static IRenderedComponent<MudButton> UpdateButton(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponents<MudButton>().Single(b => b.Find("button").TextContent.Contains("Update Attribute Flow"));

    // ─── A: the Derived chip ───

    [Test]
    public async Task Cards_DerivedFlows_WearTheDerivedChipWithTheirStepAsync()
    {
        var (_, tab) = await RenderCardsAsync(BuildRule());

        tab.WaitForState(() => tab.FindComponents<DerivedFlowChip>().Count == 2);
        Assert.That(
            tab.FindComponents<DerivedFlowChip>().Select(c => (c.Instance.StepInfo.Step, c.Instance.StepInfo.StepCount)),
            Is.EqualTo(new (int?, int)[] { (2, 3), (3, 3) }),
            "Email at step 2 and User Principal Name at step 3; Account Name is an ordinary flow");
    }

    // ─── B: Insert attribute ───

    [Test]
    public async Task EditDialog_ImportRule_OffersBothAccessorGroupsAsync()
    {
        var (provider, tab) = await RenderCardsAsync(BuildRule());
        await OpenEditAsync(tab, 1);

        provider.WaitForState(() => provider.HasComponent<InsertAttributeMenu>());
        var menu = provider.FindComponent<InsertAttributeMenu>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(menu.MetaverseAttributes, Is.Not.Null.And.Not.Empty);
            Assert.That(menu.ConnectedSystemAttributes!.Select(a => a.Name), Is.EqualTo(new[] { "firstName" }));
            Assert.That(menu.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(menu.TargetMetaverseAttributeId, Is.EqualTo(Email.Id), "the flow's own target is offered disabled");
        }
    }

    [Test]
    public async Task EditDialog_ExportRule_OffersOnlyTheMetaverseGroupAsync()
    {
        // An export expression is evaluated against the Metaverse Object alone (cs[...] resolves to nothing there).
        var (provider, tab) = await RenderCardsAsync(BuildExportRule());
        await OpenEditAsync(tab, 0);

        provider.WaitForState(() => provider.HasComponent<InsertAttributeMenu>());
        var menu = provider.FindComponent<InsertAttributeMenu>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(menu.MetaverseAttributes!.Select(a => a.Name), Is.EquivalentTo(User.Attributes.Select(a => a.Name)));
            Assert.That(menu.ConnectedSystemAttributes, Is.Null, "cs[...] resolves to nothing on export");
            Assert.That(menu.Direction, Is.EqualTo(SyncRuleDirection.Export));
        }
    }

    [Test]
    public async Task EditDialog_ChoosingAnAttribute_InsertsItsAccessorIntoTheExpressionAsync()
    {
        var rule = BuildRule();
        var (provider, tab) = await RenderCardsAsync(rule);
        await OpenEditAsync(tab, 0);
        provider.WaitForState(() => provider.HasComponent<InsertAttributeMenu>());

        var menu = provider.FindComponent<InsertAttributeMenu>();
        await menu.InvokeAsync(() => menu.Instance.OnInsert.InvokeAsync("mv[\"Department\"]"));

        // No browser caret under bUnit, so ExpressionEditor.InsertAtCursorAsync appends.
        provider.WaitForState(() => rule.AttributeFlowRules[0].Sources[0].Expression != "cs[\"firstName\"]");
        Assert.That(rule.AttributeFlowRules[0].Sources[0].Expression, Is.EqualTo("cs[\"firstName\"]mv[\"Department\"]"));
    }

    // ─── B and C: the live analysis ───

    [Test]
    public async Task EditDialog_DerivedFlow_ShowsThePanelFromTheAnalysisAsync()
    {
        var (provider, tab) = await RenderCardsAsync(BuildRule());
        await OpenEditAsync(tab, 1);

        // The analysis is debounced, so it is waited for as state rather than asserted on until it holds: NUnit records
        // every failed Assert.That, even one a retry later supersedes.
        provider.WaitForState(() => provider.FindAll("[data-testid='jim-derived-flow-panel']").Count == 1);
        var analysis = provider.FindComponent<DerivedFlowPanel>().Instance.Analysis;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(analysis?.Status, Is.EqualTo(DerivedFlowAnalysisStatus.Derived));
            Assert.That(analysis!.Step, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task EditDialog_ExpressionThatClosesALoop_ShowsTheLoopAndDisablesUpdateAsync()
    {
        var (provider, tab) = await RenderCardsAsync(BuildRule());
        await OpenEditAsync(tab, 0);
        provider.WaitForState(() => provider.FindComponents<MudButton>().Any(b => b.Find("button").TextContent.Contains("Update Attribute Flow")));
        Assert.That(UpdateButton(provider).Instance.Disabled, Is.False);

        var editor = provider.FindComponents<ExpressionEditor>().First();
        await editor.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync("mv[\"User Principal Name\"]"));

        provider.WaitForState(() => provider.Markup.Contains("This Attribute Flow cannot be saved because it creates a loop.", StringComparison.Ordinal));
        Assert.That(UpdateButton(provider).Instance.Disabled, Is.True, "the save would refuse it, so Update is not offered");
    }

    // ─── D: the dependants confirmation ───

    [Test]
    public async Task Remove_FlowDerivedFlowsRead_ShowsTheDependantsAndCancelKeepsItAsync()
    {
        var rule = BuildRule();
        var (provider, tab) = await RenderCardsAsync(rule);

        // The delete handler waits for the dialog's answer, so its click completes only once the dialog is cancelled below
        var removing = PressAsync(tab, "delete", 0);

        provider.WaitForState(() => provider.FindAll("[data-testid='jim-dependent-derived-flow-row']").Count == 2);
        await provider.Find("[data-testid='jim-dependent-derived-flows-cancel']").ClickAsync();
        await removing;

        provider.WaitForState(() => provider.FindAll("[data-testid='jim-dependent-derived-flows-confirm']").Count == 0);
        Assert.That(rule.AttributeFlowRules.Select(m => m.Id), Is.EqualTo(new[] { 1, 2, 3 }), "cancelling removes nothing");
    }

    [Test]
    public async Task Remove_FlowDerivedFlowsRead_ConfirmRemovesItAsync()
    {
        var rule = BuildRule();
        var (provider, tab) = await RenderCardsAsync(rule);

        // The delete handler waits for the dialog's answer, so its click completes only once the removal is confirmed
        var removing = PressAsync(tab, "delete", 0);
        await provider.WaitForElement("[data-testid='jim-dependent-derived-flows-confirm']").ClickAsync();
        await removing;

        tab.WaitForState(() => rule.AttributeFlowRules.Count == 2);
        Assert.That(rule.AttributeFlowRules.Select(m => m.Id), Is.EqualTo(new[] { 2, 3 }));
    }

    [Test]
    public async Task Remove_FlowNothingReads_ShowsTheOrdinaryConfirmationAsync()
    {
        var (provider, tab) = await RenderCardsAsync(BuildRule());

        // User Principal Name: no derived flow reads it. Not awaited: the delete handler waits for an answer to the
        // confirmation, which this test leaves open; the WaitForState below is what waits for the render.
        _ = PressAsync(tab, "delete", 2);

        provider.WaitForState(() => provider.Markup.Contains("Are you sure you want to remove this Attribute Mapping?", StringComparison.Ordinal));
        Assert.That(provider.FindAll("[data-testid='jim-dependent-derived-flow-row']"), Is.Empty);
    }

    [Test]
    public async Task Update_DisablingAFlowDerivedFlowsRead_AsksBeforeApplyingTheEditAsync()
    {
        var rule = BuildRule();
        var (provider, tab) = await RenderCardsAsync(rule);
        await OpenEditAsync(tab, 0);
        provider.WaitForState(() => provider.FindComponents<MudButton>().Any(b => b.Find("button").TextContent.Contains("Update Attribute Flow")));
        Assert.That(UpdateButton(provider).Instance.Disabled, Is.False);

        var enabled = provider.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.Label == "Enabled");
        await enabled.InvokeAsync(() => enabled.Instance.ValueChanged.InvokeAsync(false));
        // The update handler waits for the dependants dialog's answer, so its click completes only once that is cancelled
        var updating = UpdateButton(provider).Find("button").ClickAsync();

        provider.WaitForState(() => provider.FindAll("[data-testid='jim-dependent-derived-flows-confirm']").Count == 1);
        Assert.That(provider.Find("[data-testid='jim-dependent-derived-flows-confirm']").TextContent.Trim(), Is.EqualTo("Disable"));
        await provider.Find("[data-testid='jim-dependent-derived-flows-cancel']").ClickAsync();
        await updating;
        provider.WaitForState(() => provider.FindAll("[data-testid='jim-dependent-derived-flows-confirm']").Count == 0);
        Assert.That(UpdateButton(provider).Instance.Disabled, Is.False, "the edit dialog stays open for another go");
    }

    // ─── Generated Value Forms (#2014, mock screen 3): the Derived Mapping Type filter ───

    [Test]
    public void Table_MappingTypeFilter_OffersDerivedApartFromOrdinaryExpressions()
    {
        Render<MudPopoverProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, BuildRule()));

        var chips = tab.FindComponent<MudChipSet<AttributeFlowMappingType>>().FindComponents<MudChip<AttributeFlowMappingType>>()
            .ToDictionary(c => c.Instance.Value, c => c.Markup);

        // Account Name reads cs[...] only; Email and User Principal Name read the Metaverse.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chips.Keys, Is.EqualTo(new[] { AttributeFlowMappingType.ExpressionMapping, AttributeFlowMappingType.Derived }));
            Assert.That(chips[AttributeFlowMappingType.ExpressionMapping], Does.Contain("(1)"));
            Assert.That(chips[AttributeFlowMappingType.Derived], Does.Contain("(2)"));
        }
    }

    [Test]
    public async Task Table_DerivedAndGeneratedSelected_ListsBothKindsAndNothingElseAsync()
    {
        var rule = BuildRule();
        rule.AttributeFlowRules[0].Generation = new SyncRuleMappingGeneration { Id = 90, TokenKind = GeneratedValueTokenKind.OnlyIfTaken };
        AddExpression(rule, 4, Department, "Upper(cs[\"firstName\"])");
        Render<MudPopoverProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, rule));

        var chipSet = tab.FindComponent<MudChipSet<AttributeFlowMappingType>>();
        await tab.InvokeAsync(() => chipSet.Instance.SelectedValuesChanged.InvokeAsync(
            [AttributeFlowMappingType.Derived, AttributeFlowMappingType.GeneratedMapping]));
        var grid = tab.FindComponent<VirtualisedDataGrid<SyncRuleMapping>>().Instance;
        var window = await grid.LoadWindow(new VirtualisedWindowRequest(0, 50, null, "target", false, true), CancellationToken.None);

        Assert.That(window.Items.Select(m => m.Id), Is.EquivalentTo(new[] { 1, 2, 3 }),
            "the generated Account Name and the derived Email and User Principal Name; not the ordinary Department expression");
    }

    // ─── Generated Value Forms (#2014, mock screen 1): the "Built from generated value" block ───

    [Test]
    public async Task EditDialog_FlowBuiltFromAGeneratedValue_ShowsWhichOneWithItsKeepUniqueSwitchAsync()
    {
        var rule = BuildRule();
        rule.AttributeFlowRules[0].Generation = new SyncRuleMappingGeneration { Id = 90, TokenKind = GeneratedValueTokenKind.OnlyIfTaken };
        var (provider, tab) = await RenderCardsAsync(rule);

        // Email reads the generated Account Name.
        await OpenEditAsync(tab, 1);

        provider.WaitForState(() => provider.HasComponent<BuiltFromGeneratedValueBlock>()
            && provider.FindComponent<BuiltFromGeneratedValueBlock>().Instance.Description != null);
        var block = provider.FindComponent<BuiltFromGeneratedValueBlock>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Description!.GeneratedAttributeName, Is.EqualTo("Account Name"));
            Assert.That(block.Mapping, Is.SameAs(rule.AttributeFlowRules[1]), "the switch edits the flow being edited");
            Assert.That(provider.FindAll("[data-testid='built-from-generated-value-keep-unique']"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task EditDialog_FlowBuiltFromNoGeneratedValue_ShowsNoBlockAsync()
    {
        // Nothing in the rule is generated, so Email is an ordinary derived flow.
        var (provider, tab) = await RenderCardsAsync(BuildRule());
        await OpenEditAsync(tab, 1);

        provider.WaitForState(() => provider.HasComponent<DerivedFlowPanel>()
            && provider.FindComponent<DerivedFlowPanel>().Instance.Analysis != null);
        Assert.That(provider.FindAll("[data-testid='built-from-generated-value']"), Is.Empty);
    }

    // ─── scaffolding ───

    private sealed class FreshJimApplicationFactory : IJimApplicationFactory
    {
        public Func<IRepository> BuildRepository { get; set; } = null!;

        public Func<ISyncRepository> BuildSyncRepository { get; set; } = null!;

        public JimApplication Create() => new(BuildRepository(), syncRepository: BuildSyncRepository());
    }
}
