// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
using JIM.TestSupport;
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
/// the loop that disables Update), and the confirmation before removing a flow derived flows read. With the feature
/// off, none of it appears.
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
    private bool _flagEnabled = true;

    private static readonly MetaverseAttribute AccountName = new() { Id = 10, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private static readonly MetaverseAttribute Email = new() { Id = 11, Name = "Email", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private static readonly MetaverseAttribute UserPrincipalName = new() { Id = 12, Name = "User Principal Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private static readonly MetaverseAttribute Department = new() { Id = 13, Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
    private static readonly MetaverseObjectType User = new() { Id = UserTypeId, Name = "User", PluralName = "Users", Attributes = [AccountName, Email, UserPrincipalName, Department] };

    protected override void ConfigureAdditionalServices()
    {
        _csRepo = new Mock<IConnectedSystemRepository>();
        _mvRepo = new Mock<IMetaverseRepository>();
        _factory.BuildRepository = () =>
        {
            var repo = new Mock<IRepository>();
            repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
            repo.Setup(r => r.Metaverse).Returns(_mvRepo.Object);
            repo.Setup(r => r.ServiceSettings).Returns(_flagEnabled
                ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
                : new InMemoryServiceSettingsRepository());
            return repo.Object;
        };
        Services.AddSingleton<IJimApplicationFactory>(_factory);
        Services.AddSingleton<IExpressionEvaluator, DynamicExpressoEvaluator>();
    }

    [SetUp]
    public void SetUp()
    {
        _flagEnabled = true;
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

    private (IRenderedComponent<MudDialogProvider> Provider, IRenderedComponent<SyncRuleAttributeFlowTab> Tab) RenderCards(SyncRule rule)
    {
        var provider = Render<MudDialogProvider>();
        Render<MudPopoverProvider>();
        var tab = Render<SyncRuleAttributeFlowTab>(p => p.Add(c => c.SyncRule, rule));
        // The card view draws every flow; the table is a virtualised grid that draws rows from the browser's
        // measurements, which bUnit does not have.
        tab.InvokeAsync(() => tab.FindComponents<MudIconButton>().Single(b => b.Instance.Icon == Icons.Material.Filled.GridView).Find("button").Click());
        tab.WaitForState(() => tab.FindAll("button[aria-label='edit']").Count == rule.AttributeFlowRules.Count);
        return (provider, tab);
    }

    private static void OpenEdit(IRenderedComponent<SyncRuleAttributeFlowTab> tab, int mappingIndex) => Press(tab, "edit", mappingIndex);

    private static void Press(IRenderedComponent<SyncRuleAttributeFlowTab> tab, string action, int mappingIndex) =>
        tab.InvokeAsync(() => tab.FindAll($"button[aria-label='{action}']")[mappingIndex].Click());

    private static IRenderedComponent<MudButton> UpdateButton(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindComponents<MudButton>().Single(b => b.Find("button").TextContent.Contains("Update Attribute Flow"));

    // ─── A: the Derived chip ───

    [Test]
    public void Cards_DerivedFlows_WearTheDerivedChipWithTheirStep()
    {
        var (_, tab) = RenderCards(BuildRule());

        tab.WaitForState(() => tab.FindComponents<DerivedFlowChip>().Count == 2);
        Assert.That(
            tab.FindComponents<DerivedFlowChip>().Select(c => (c.Instance.StepInfo.Step, c.Instance.StepInfo.StepCount)),
            Is.EqualTo(new (int?, int)[] { (2, 3), (3, 3) }),
            "Email at step 2 and User Principal Name at step 3; Account Name is an ordinary flow");
    }

    [Test]
    public void Cards_FlagOff_ShowNoDerivedChip()
    {
        _flagEnabled = false;
        var (_, tab) = RenderCards(BuildRule());

        Assert.That(tab.HasComponent<DerivedFlowChip>(), Is.False);
    }

    // ─── B: Insert attribute ───

    [Test]
    public void EditDialog_ImportRule_OffersBothAccessorGroups()
    {
        var (provider, tab) = RenderCards(BuildRule());
        OpenEdit(tab, 1);

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
    public void EditDialog_FlagOff_OffersOnlyTheConnectedSystemGroup()
    {
        _flagEnabled = false;
        var (provider, tab) = RenderCards(BuildRule());
        OpenEdit(tab, 1);

        provider.WaitForState(() => provider.HasComponent<InsertAttributeMenu>());
        Assert.That(provider.FindComponent<InsertAttributeMenu>().Instance.MetaverseAttributes, Is.Null);
    }

    [Test]
    public void EditDialog_ChoosingAnAttribute_InsertsItsAccessorIntoTheExpression()
    {
        var rule = BuildRule();
        var (provider, tab) = RenderCards(rule);
        OpenEdit(tab, 0);
        provider.WaitForState(() => provider.HasComponent<InsertAttributeMenu>());

        var menu = provider.FindComponent<InsertAttributeMenu>();
        menu.InvokeAsync(() => menu.Instance.OnInsert.InvokeAsync("mv[\"Department\"]"));

        // No browser caret under bUnit, so ExpressionEditor.InsertAtCursorAsync appends.
        provider.WaitForState(() => rule.AttributeFlowRules[0].Sources[0].Expression != "cs[\"firstName\"]");
        Assert.That(rule.AttributeFlowRules[0].Sources[0].Expression, Is.EqualTo("cs[\"firstName\"]mv[\"Department\"]"));
    }

    // ─── B and C: the live analysis ───

    [Test]
    public void EditDialog_DerivedFlow_ShowsThePanelFromTheAnalysis()
    {
        var (provider, tab) = RenderCards(BuildRule());
        OpenEdit(tab, 1);

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
    public void EditDialog_ExpressionThatClosesALoop_ShowsTheLoopAndDisablesUpdate()
    {
        var (provider, tab) = RenderCards(BuildRule());
        OpenEdit(tab, 0);
        provider.WaitForState(() => provider.FindComponents<MudButton>().Any(b => b.Find("button").TextContent.Contains("Update Attribute Flow")));
        Assert.That(UpdateButton(provider).Instance.Disabled, Is.False);

        var editor = provider.FindComponents<ExpressionEditor>().First();
        editor.InvokeAsync(() => editor.Instance.ValueChanged.InvokeAsync("mv[\"User Principal Name\"]"));

        provider.WaitForState(() => provider.Markup.Contains("This Attribute Flow cannot be saved because it creates a loop.", StringComparison.Ordinal));
        Assert.That(UpdateButton(provider).Instance.Disabled, Is.True, "the save would refuse it, so Update is not offered");
    }

    [Test]
    public void EditDialog_FlagOff_RunsNoAnalysis()
    {
        _flagEnabled = false;
        var (provider, tab) = RenderCards(BuildRule());
        OpenEdit(tab, 1);

        provider.WaitForState(() => provider.HasComponent<InsertAttributeMenu>());
        Assert.That(provider.FindComponent<DerivedFlowPanel>().Instance.Analysis, Is.Null);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    // ─── D: the dependants confirmation ───

    [Test]
    public void Remove_FlowDerivedFlowsRead_ShowsTheDependantsAndCancelKeepsIt()
    {
        var rule = BuildRule();
        var (provider, tab) = RenderCards(rule);

        Press(tab, "delete", 0);

        provider.WaitForState(() => provider.FindAll("[data-testid='jim-dependent-derived-flow-row']").Count == 2);
        provider.Find("[data-testid='jim-dependent-derived-flows-cancel']").Click();

        provider.WaitForState(() => provider.FindAll("[data-testid='jim-dependent-derived-flows-confirm']").Count == 0);
        Assert.That(rule.AttributeFlowRules.Select(m => m.Id), Is.EqualTo(new[] { 1, 2, 3 }), "cancelling removes nothing");
    }

    [Test]
    public void Remove_FlowDerivedFlowsRead_ConfirmRemovesIt()
    {
        var rule = BuildRule();
        var (provider, tab) = RenderCards(rule);

        Press(tab, "delete", 0);
        provider.WaitForElement("[data-testid='jim-dependent-derived-flows-confirm']").Click();

        tab.WaitForState(() => rule.AttributeFlowRules.Count == 2);
        Assert.That(rule.AttributeFlowRules.Select(m => m.Id), Is.EqualTo(new[] { 2, 3 }));
    }

    [Test]
    public void Remove_FlowNothingReads_ShowsTheOrdinaryConfirmation()
    {
        var (provider, tab) = RenderCards(BuildRule());

        // User Principal Name: no derived flow reads it.
        Press(tab, "delete", 2);

        provider.WaitForState(() => provider.Markup.Contains("Are you sure you want to remove this Attribute Mapping?", StringComparison.Ordinal));
        Assert.That(provider.FindAll("[data-testid='jim-dependent-derived-flow-row']"), Is.Empty);
    }

    [Test]
    public void Remove_FlagOff_ShowsTheOrdinaryConfirmationAndReadsNothing()
    {
        _flagEnabled = false;
        var (provider, tab) = RenderCards(BuildRule());

        Press(tab, "delete", 0);

        provider.WaitForState(() => provider.Markup.Contains("Are you sure you want to remove this Attribute Mapping?", StringComparison.Ordinal));
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public void Update_DisablingAFlowDerivedFlowsRead_AsksBeforeApplyingTheEdit()
    {
        var rule = BuildRule();
        var (provider, tab) = RenderCards(rule);
        OpenEdit(tab, 0);
        provider.WaitForState(() => provider.FindComponents<MudButton>().Any(b => b.Find("button").TextContent.Contains("Update Attribute Flow")));
        Assert.That(UpdateButton(provider).Instance.Disabled, Is.False);

        var enabled = provider.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.Label == "Enabled");
        enabled.InvokeAsync(() => enabled.Instance.ValueChanged.InvokeAsync(false));
        UpdateButton(provider).Find("button").Click();

        provider.WaitForState(() => provider.FindAll("[data-testid='jim-dependent-derived-flows-confirm']").Count == 1);
        Assert.That(provider.Find("[data-testid='jim-dependent-derived-flows-confirm']").TextContent.Trim(), Is.EqualTo("Disable"));
        provider.Find("[data-testid='jim-dependent-derived-flows-cancel']").Click();
        provider.WaitForState(() => provider.FindAll("[data-testid='jim-dependent-derived-flows-confirm']").Count == 0);
        Assert.That(UpdateButton(provider).Instance.Disabled, Is.False, "the edit dialog stays open for another go");
    }

    // ─── scaffolding ───

    private sealed class FreshJimApplicationFactory : IJimApplicationFactory
    {
        public Func<IRepository> BuildRepository { get; set; } = null!;

        public JimApplication Create() => new(BuildRepository());
    }
}
