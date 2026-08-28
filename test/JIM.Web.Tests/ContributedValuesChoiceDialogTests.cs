// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the recall-or-keep choice dialog shown when deleting a Synchronisation Rule, or removing an
/// Attribute Flow mapping, that contributed current Metaverse attribute values (#1537). The behaviour that
/// matters: recall must be the pre-selected default on every surface, the keep warning must only appear once
/// keep is chosen, and the result must faithfully carry the choice (or the cancellation) back to the caller,
/// because the caller acts irreversibly on it.
/// </summary>
[TestFixture]
public class ContributedValuesChoiceDialogTests : JimComponentTestContext
{
    private const string ConfirmButtonMarker = "jim-recall-choice-confirm";
    private const string CancelButtonMarker = "jim-recall-choice-cancel";
    private const string KeepWarningMarker = "jim-recall-choice-keep-warning";
    private const string InfoAlertMarker = "jim-recall-choice-info";

    private static ContributedValuesSummary BuildRuleSummary() => new()
    {
        TotalObjects = 1200,
        Attributes =
        [
            new ContributedValuesAttributeSummary { AttributeId = 1, AttributeName = "Display Name", ValueCount = 1200, ObjectCount = 1200 },
            new ContributedValuesAttributeSummary { AttributeId = 2, AttributeName = "Department", ValueCount = 340, ObjectCount = 340 }
        ]
    };

    /// <summary>
    /// Opens the dialog through the static helper callers use, capturing the result task so tests can assert
    /// what the caller would receive. Waiting for the confirm button makes the dispatch deterministic (see
    /// <see cref="ConsequenceConfirmationDialogTests"/> for the reasoning).
    /// </summary>
    private (IRenderedComponent<MudDialogProvider> Provider, Task<(bool Cancelled, bool KeepContributedValues)> Result) ShowRuleDialog()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        Task<(bool Cancelled, bool KeepContributedValues)> result = null!;
        provider.InvokeAsync(() => { result = ContributedValuesChoiceDialog.ShowForSyncRuleDeleteAsync(dialogService, BuildRuleSummary()); });
        provider.WaitForElement($"[data-testid='{ConfirmButtonMarker}']");
        return (provider, result);
    }

    private (IRenderedComponent<MudDialogProvider> Provider, Task<(bool Cancelled, bool KeepContributedValues)> Result) ShowMappingDialog()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        Task<(bool Cancelled, bool KeepContributedValues)> result = null!;
        provider.InvokeAsync(() => { result = ContributedValuesChoiceDialog.ShowForMappingRemovalAsync(dialogService, "Display Name", 1200); });
        provider.WaitForElement($"[data-testid='{ConfirmButtonMarker}']");
        return (provider, result);
    }

    /// <summary>
    /// Selects the keep radio. MudRadio's input commits on click rather than change, so a click on the keep
    /// input is what selecting it produces.
    /// </summary>
    private static void ChooseKeep(IRenderedComponent<MudDialogProvider> provider)
    {
        var radios = provider.FindAll("input[type='radio']");
        Assert.That(radios, Has.Count.EqualTo(2), "expected exactly the recall and keep radios");
        radios[1].Click();
    }

    #region Rule delete variant

    [Test]
    public void ContributedValuesChoiceDialog_RuleDelete_StatesImpactCountsAndAttributes()
    {
        var (provider, _) = ShowRuleDialog();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Markup, Does.Contain("This rule contributed the current values of 1,540 attribute value(s) across 1,200 Metaverse Objects."));
            Assert.That(provider.Markup, Does.Contain("Display Name"));
            Assert.That(provider.Markup, Does.Contain("Department"));
        }
    }

    [Test]
    public void ContributedValuesChoiceDialog_RuleDelete_DefaultsToRecall()
    {
        var (provider, _) = ShowRuleDialog();

        var radios = provider.FindAll("input[type='radio']");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(radios[0].HasAttribute("checked"), Is.True, "recall must be pre-selected");
            Assert.That(provider.FindAll($"[data-testid='{KeepWarningMarker}']"), Is.Empty,
                "the keep warning must not show while recall is selected");
        }
    }

    [Test]
    public void ContributedValuesChoiceDialog_RuleDelete_ChoosingKeepRevealsWarning()
    {
        var (provider, _) = ShowRuleDialog();

        ChooseKeep(provider);

        var warning = provider.WaitForElement($"[data-testid='{KeepWarningMarker}']");
        Assert.That(warning.TextContent, Does.Contain("Kept values become permanently unmanaged. This cannot be reversed once the rule is deleted."));
    }

    [Test]
    public void ContributedValuesChoiceDialog_RuleDelete_HasNoSaveTimingInfoAlert()
    {
        // The rule delete acts immediately; only the mapping removal is staged until the rule is saved.
        var (provider, _) = ShowRuleDialog();

        Assert.That(provider.FindAll($"[data-testid='{InfoAlertMarker}']"), Is.Empty);
    }

    [Test]
    public async Task ContributedValuesChoiceDialog_RuleDelete_ConfirmWithDefault_ReturnsRecallAsync()
    {
        var (provider, result) = ShowRuleDialog();

        provider.Find($"[data-testid='{ConfirmButtonMarker}']").Click();

        var outcome = await result;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Cancelled, Is.False);
            Assert.That(outcome.KeepContributedValues, Is.False);
        }
    }

    [Test]
    public async Task ContributedValuesChoiceDialog_RuleDelete_ConfirmWithKeep_ReturnsKeepAsync()
    {
        var (provider, result) = ShowRuleDialog();

        ChooseKeep(provider);
        provider.Find($"[data-testid='{ConfirmButtonMarker}']").Click();

        var outcome = await result;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Cancelled, Is.False);
            Assert.That(outcome.KeepContributedValues, Is.True);
        }
    }

    [Test]
    public async Task ContributedValuesChoiceDialog_RuleDelete_Cancel_ReturnsCancelledAsync()
    {
        var (provider, result) = ShowRuleDialog();

        provider.Find($"[data-testid='{CancelButtonMarker}']").Click();

        var outcome = await result;
        Assert.That(outcome.Cancelled, Is.True);
    }

    [Test]
    public void ContributedValuesChoiceDialog_RuleDelete_ConfirmButtonNamesTheAction()
    {
        var (provider, _) = ShowRuleDialog();

        Assert.That(provider.Find($"[data-testid='{ConfirmButtonMarker}']").TextContent.Trim(),
            Is.EqualTo("Delete Synchronisation Rule"));
    }

    #endregion

    #region Mapping removal variant

    [Test]
    public void ContributedValuesChoiceDialog_MappingRemoval_StatesAttributeAndObjectCount()
    {
        var (provider, _) = ShowMappingDialog();

        Assert.That(provider.Markup, Does.Contain("This Attribute Flow contributed the current values of Display Name on 1,200 Metaverse Objects."));
    }

    [Test]
    public void ContributedValuesChoiceDialog_MappingRemoval_StatesTheChoiceAppliesOnSave()
    {
        var (provider, _) = ShowMappingDialog();

        var info = provider.Find($"[data-testid='{InfoAlertMarker}']");
        Assert.That(info.TextContent, Does.Contain("Your choice takes effect when you save the Synchronisation Rule."));
    }

    [Test]
    public void ContributedValuesChoiceDialog_MappingRemoval_ChoosingKeepRevealsMappingWordedWarning()
    {
        var (provider, _) = ShowMappingDialog();

        ChooseKeep(provider);

        var warning = provider.WaitForElement($"[data-testid='{KeepWarningMarker}']");
        Assert.That(warning.TextContent, Does.Contain("Kept values become permanently unmanaged. This cannot be reversed once the mapping is removed."));
    }

    [Test]
    public void ContributedValuesChoiceDialog_MappingRemoval_ConfirmButtonNamesTheAction()
    {
        var (provider, _) = ShowMappingDialog();

        Assert.That(provider.Find($"[data-testid='{ConfirmButtonMarker}']").TextContent.Trim(),
            Is.EqualTo("Remove Attribute Mapping"));
    }

    [Test]
    public async Task ContributedValuesChoiceDialog_MappingRemoval_ConfirmWithKeep_ReturnsKeepAsync()
    {
        var (provider, result) = ShowMappingDialog();

        ChooseKeep(provider);
        provider.Find($"[data-testid='{ConfirmButtonMarker}']").Click();

        var outcome = await result;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Cancelled, Is.False);
            Assert.That(outcome.KeepContributedValues, Is.True);
        }
    }

    #endregion
}
