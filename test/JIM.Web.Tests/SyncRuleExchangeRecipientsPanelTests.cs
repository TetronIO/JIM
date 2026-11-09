// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Models.Core;
using JIM.Models.Exchange;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Models;
using JIM.Web.Pages.Admin.Components;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Exchange recipients panel on an export Synchronisation Rule's Attribute Flow tab (#2021, PRD requirement 4): the
/// way in to setting up recipients. Every action it offers is an Attribute Flow operation the tab already performs, so
/// these tests pin which action it offers where, and that each hands the tab the right attribute or flow.
/// </summary>
[TestFixture]
public class SyncRuleExchangeRecipientsPanelTests : JimComponentTestContext
{
    private SyncRule _rule = null!;
    private readonly List<ConnectedSystemObjectTypeAttribute> _set = [];
    private readonly List<SyncRuleMapping> _edited = [];
    private readonly List<SyncRuleMapping> _removed = [];
    private int _stopped;

    [SetUp]
    public void SetUp()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "user" };
        var id = 10;
        foreach (var name in ExchangeRecipientAttributes.All)
            objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = id++, Name = name, Type = AttributeDataType.Text, IsConnectorDefined = true });
        _rule = new SyncRule { Name = "PANOPLY Export Users", Direction = SyncRuleDirection.Export, ConnectedSystemObjectType = objectType };
        _set.Clear();
        _edited.Clear();
        _removed.Clear();
        _stopped = 0;
    }

    [Test]
    public async Task NotSetUp_OffersSetUpRecipients_WhichStartsWithTheRecipientTypeAsync()
    {
        var cut = RenderPanel();

        await cut.Find("[data-testid='jim-exchange-recipients-set-up']").ClickAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid='jim-exchange-recipients-row']"), Is.Empty, "no table until something is set");
            Assert.That(_set.Select(a => a.Name), Is.EqualTo(new[] { ExchangeRecipientAttributes.RecipientType }));
        }
    }

    [Test]
    public async Task SetUp_RowWithAFlow_OffersEditAndRemoveForThatFlowAsync()
    {
        var alias = Flow(ExchangeRecipientAttributes.Alias);
        var cut = RenderPanel();

        var row = AliasRow(cut);
        await row.QuerySelector("[data-testid='jim-exchange-recipients-edit']")!.ClickAsync();
        await AliasRow(cut).QuerySelector("[data-testid='jim-exchange-recipients-remove']")!.ClickAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_edited, Is.EqualTo(new[] { alias }));
            Assert.That(_removed, Is.EqualTo(new[] { alias }));
            Assert.That(AliasRow(cut).QuerySelector("[data-testid='jim-exchange-recipients-set']"), Is.Null, "a set attribute is edited, not set again");
        }
    }

    [Test]
    public async Task SetUp_RowWithoutAFlow_SaysWhatHappensAndOffersSetForThatAttributeAsync()
    {
        Flow(ExchangeRecipientAttributes.RecipientType);
        var cut = RenderPanel();

        var databaseRow = cut.FindAll("[data-testid='jim-exchange-recipients-row']")
            .Single(r => r.TextContent.Contains(ExchangeRecipientAttributes.Database));
        await databaseRow.QuerySelector("[data-testid='jim-exchange-recipients-set']")!.ClickAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(databaseRow.TextContent, Does.Contain("Not set, so Exchange chooses"));
            Assert.That(databaseRow.QuerySelector("[data-testid='jim-exchange-recipients-edit']"), Is.Null);
            Assert.That(_set.Select(a => a.Name), Is.EqualTo(new[] { ExchangeRecipientAttributes.Database }));
        }
    }

    [Test]
    public async Task Stop_Confirmed_RaisesStopOnceAsync()
    {
        Flow(ExchangeRecipientAttributes.RecipientType);
        Flow(ExchangeRecipientAttributes.Alias);
        var provider = Render<MudDialogProvider>();
        var cut = RenderPanel();

        var stopping = cut.Find("[data-testid='jim-exchange-recipients-stop']").ClickAsync();
        var confirm = provider.WaitForElement("[data-testid='jim-exchange-recipients-stop-confirm']");
        var confirmText = confirm.TextContent;
        var dialogText = provider.Markup;
        await confirm.ClickAsync();
        await stopping;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_stopped, Is.EqualTo(1));
            Assert.That(confirmText, Does.Contain("2"), "the confirmation names how many Attribute Flows go");
            Assert.That(dialogText, Does.Contain("PANOPLY Export Users"));
        }
    }

    [Test]
    public async Task Stop_Cancelled_DoesNothingAsync()
    {
        Flow(ExchangeRecipientAttributes.RecipientType);
        var provider = Render<MudDialogProvider>();
        var cut = RenderPanel();

        var stopping = cut.Find("[data-testid='jim-exchange-recipients-stop']").ClickAsync();
        await provider.WaitForElement("[data-testid='jim-exchange-recipients-stop-cancel']").ClickAsync();
        await stopping;

        Assert.That(_stopped, Is.Zero);
    }

    private IRenderedComponent<SyncRuleExchangeRecipientsPanel> RenderPanel() =>
        Render<SyncRuleExchangeRecipientsPanel>(p => p
            .Add(c => c.Flows, ExchangeRecipientFlows.For(_rule)!)
            .Add(c => c.SyncRuleName, _rule.Name)
            .Add(c => c.OnSet, attribute => _set.Add(attribute))
            .Add(c => c.OnEdit, mapping => _edited.Add(mapping))
            .Add(c => c.OnRemove, mapping => _removed.Add(mapping))
            .Add(c => c.OnStop, () => _stopped++));

    private static AngleSharp.Dom.IElement AliasRow(IRenderedComponent<SyncRuleExchangeRecipientsPanel> cut) =>
        cut.FindAll("[data-testid='jim-exchange-recipients-row']").Single(r => r.TextContent.Contains(ExchangeRecipientAttributes.Alias));

    private SyncRuleMapping Flow(string attributeName)
    {
        var attribute = _rule.ConnectedSystemObjectType.Attributes.Single(a => a.Name == attributeName);
        var mapping = new SyncRuleMapping { Id = attribute.Id + 100, TargetConnectedSystemAttribute = attribute, TargetConnectedSystemAttributeId = attribute.Id };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "\"Mailbox\"" });
        _rule.AttributeFlowRules.Add(mapping);
        return mapping;
    }
}
