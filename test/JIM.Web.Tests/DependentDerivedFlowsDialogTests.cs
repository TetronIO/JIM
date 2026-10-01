// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic.DTOs;
using JIM.Web.Pages.Admin.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The confirmation shown before a change leaves Metaverse-Derived Attribute Flows without an input (#1750, FR 3,
/// mockup D): which flows, on which Synchronisation Rule, reading what; Cancel backs out, the named action goes ahead.
/// </summary>
[TestFixture]
public class DependentDerivedFlowsDialogTests : JimComponentTestContext
{
    private static readonly IReadOnlyList<DependentDerivedFlow> Dependants =
    [
        new()
        {
            MappingId = 2, TargetMetaverseAttributeName = "Email", SyncRuleId = 12, SyncRuleName = "HR Inbound",
            ConnectedSystemId = 1, ConnectedSystemName = "HR",
            MissingInputs = [new DependentDerivedFlowInput { MetaverseAttributeName = "Account Name" }]
        },
        new()
        {
            MappingId = 3, TargetMetaverseAttributeName = "User Principal Name", SyncRuleId = 12, SyncRuleName = "HR Inbound",
            ConnectedSystemId = 1, ConnectedSystemName = "HR",
            MissingInputs = [new DependentDerivedFlowInput { MetaverseAttributeName = "Account Name", Indirect = true, Via = ["Email"] }]
        }
    ];

    private (IRenderedComponent<MudDialogProvider> Provider, Task<bool> Result) Show()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        Task<bool> result = null!;
        provider.InvokeAsync(() => { result = DependentDerivedFlowsDialog.ConfirmAsync(
            dialogService, "Remove Attribute Flow?", "remove the Attribute Flow to Account Name", Dependants, "Remove"); });
        provider.WaitForElement("[data-testid='jim-dependent-derived-flows-confirm']");
        return (provider, result);
    }

    [Test]
    public void DependentDerivedFlowsDialog_ListsEachDependantWithItsRuleAndWhatItReads()
    {
        var (provider, _) = Show();

        var rows = provider.FindAll("[data-testid='jim-dependent-derived-flow-row']")
            .Select(row => string.Join(" | ", row.QuerySelectorAll("td").Select(td => td.TextContent.Trim())))
            .ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.Find("[data-testid='jim-dependent-derived-flows-intro']").TextContent.Trim(), Is.EqualTo(
                "After you remove the Attribute Flow to Account Name, nothing else contributes Account Name. " +
                "These Derived Attribute Flows read it, so they will have no input:"));
            Assert.That(rows, Is.EqualTo(new[]
            {
                "→ Email | HR Inbound | Account Name",
                "→ User Principal Name | HR Inbound | Email (via Account Name)"
            }));
            Assert.That(provider.Find("[data-testid='jim-dependent-derived-flows-confirm']").TextContent.Trim(), Is.EqualTo("Remove"));
        }
    }

    [Test]
    public async Task DependentDerivedFlowsDialog_Confirm_ReturnsTrueAsync()
    {
        var (provider, result) = Show();

        provider.Find("[data-testid='jim-dependent-derived-flows-confirm']").Click();

        Assert.That(await result, Is.True);
    }

    [Test]
    public async Task DependentDerivedFlowsDialog_Cancel_ReturnsFalseAsync()
    {
        var (provider, result) = Show();

        provider.Find("[data-testid='jim-dependent-derived-flows-cancel']").Click();

        Assert.That(await result, Is.False);
    }
}
