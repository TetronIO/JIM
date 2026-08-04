// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers how a configuration change preflight is turned into something an administrator can consent to. The dialog
/// carries no gating rules of its own (those belong to the shared ConsequenceConfirmationDialog it wraps); what it
/// owns is the composition, and getting that wrong means either warning about deletion that is not happening or
/// staying quiet about deletion that is.
/// </summary>
[TestFixture]
public class ConfigurationChangeAcknowledgementDialogTests : JimComponentTestContext
{
    private const string ConfirmButtonMarker = "jim-consequence-confirm";

    private IRenderedComponent<MudDialogProvider> ShowDialog(ConfigurationChangePreflight preflight)
    {
        var parameters = new DialogParameters<ConfigurationChangeAcknowledgementDialog>
        {
            { x => x.Preflight, preflight },
            { x => x.ObjectTypeLabel, "Synchronisation Rule" },
            { x => x.ObjectName, "HR Inbound" }
        };

        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        provider.InvokeAsync(() => dialogService.ShowAsync<ConfigurationChangeAcknowledgementDialog>("Confirm", parameters));
        provider.WaitForElement($"[data-testid='{ConfirmButtonMarker}']");

        return provider;
    }

    [Test]
    public void AcknowledgementDialog_DestructiveChange_StatesTheConsequence()
    {
        var provider = ShowDialog(Preflight(ConfigurationChangeClass.Destructive, Item(
            "Outbound Deprovision Action", ConfigurationChangeClass.Destructive,
            consequence: "Objects this rule deprovisions will be deleted in the Connected System.")));

        Assert.That(provider.Markup, Does.Contain("will be deleted in the Connected System"),
            "the administrator is being asked to consent to deletion, so the dialog must say so");
    }

    [Test]
    public void AcknowledgementDialog_DestructiveChangeThatRemovesRisk_DoesNotAnnounceDataLoss()
    {
        // Switching a destructive property back is still Class A, so the dialog still appears; but the framing it
        // leads with must not contradict the consequence beneath it.
        var provider = ShowDialog(Preflight(ConfigurationChangeClass.Destructive, Item(
            "Inbound out-of-scope action", ConfigurationChangeClass.Destructive,
            consequence: "Objects that fall out of this rule's scope will stay joined to their Metaverse Objects.")));

        Assert.Multiple(() =>
        {
            Assert.That(provider.Markup, Does.Contain("decides whether objects are removed"),
                "the headline should name what the property governs, not assert a direction");
            Assert.That(provider.Markup, Does.Not.Contain("can remove data"),
                "claiming data loss over a change that prevents it is how a dialog earns reflex dismissal");
        });
    }

    [Test]
    public void AcknowledgementDialog_SyncAffectingChange_DoesNotClaimDataWillBeRemoved()
    {
        var provider = ShowDialog(Preflight(ConfigurationChangeClass.SyncAffecting,
            Item("Enabled", ConfigurationChangeClass.SyncAffecting)));

        Assert.That(provider.Markup, Does.Not.Contain("whether objects are removed"),
            "a rule being disabled removes nothing on its own; saying otherwise trains administrators to dismiss the dialog");
    }

    [Test]
    public void AcknowledgementDialog_AnyChange_RecommendsAFullSynchronisation()
    {
        // The whole point of the interim messaging: saving is not applying.
        var provider = ShowDialog(Preflight(ConfigurationChangeClass.SyncAffecting,
            Item("Enabled", ConfigurationChangeClass.SyncAffecting)));

        Assert.That(provider.Markup, Does.Contain("Full Synchronisation"));
    }

    [Test]
    public void AcknowledgementDialog_NamesTheObjectBeingChangedInTheAccentColour()
    {
        var provider = ShowDialog(Preflight(ConfigurationChangeClass.SyncAffecting,
            Item("Enabled", ConfigurationChangeClass.SyncAffecting)));

        Assert.That(provider.Markup, Does.Contain("<span class=\"mud-primary-text\">HR Inbound</span>"),
            "the object's own name should stand out from the type label beside it");
    }

    [Test]
    public void AcknowledgementDialog_SeveralChanges_UsesThePluralHeading()
    {
        var provider = ShowDialog(Preflight(ConfigurationChangeClass.SyncAffecting,
            Item("Enabled", ConfigurationChangeClass.SyncAffecting),
            Item("Direction", ConfigurationChangeClass.SyncAffecting)));

        Assert.That(provider.Markup, Does.Contain("Changes to"));
    }

    [Test]
    public void AcknowledgementDialog_AnyChange_ListsWhatIsChangingWithBothValues()
    {
        var provider = ShowDialog(Preflight(ConfigurationChangeClass.SyncAffecting,
            Item("Enabled", ConfigurationChangeClass.SyncAffecting, oldValue: "Yes", newValue: "No")));

        Assert.Multiple(() =>
        {
            Assert.That(provider.Markup, Does.Contain("Enabled"));
            Assert.That(provider.Markup, Does.Contain("Yes"));
            Assert.That(provider.Markup, Does.Contain("No"));
        });
    }

    [Test]
    public void AcknowledgementDialog_SecretChange_ReportsItChangedWithoutAValue()
    {
        // A secret is detected by hash and never carries values; rendering an empty "to" would read as cleared.
        var provider = ShowDialog(Preflight(ConfigurationChangeClass.SyncAffecting,
            Item("Password", ConfigurationChangeClass.SyncAffecting)));

        Assert.That(provider.Markup, Does.Contain("Changed"));
    }

    [Test]
    public void AcknowledgementDialog_ManyChanges_CountsTheOnesItDoesNotList()
    {
        var items = Enumerable.Range(1, 20)
            .Select(i => Item($"Property {i:00}", ConfigurationChangeClass.SyncAffecting))
            .ToArray();

        var provider = ShowDialog(Preflight(ConfigurationChangeClass.SyncAffecting, items));

        Assert.That(provider.Markup, Does.Contain("8 more"),
            "a wall of rows is a dialog nobody reads; the tail should be counted rather than listed");
    }

    private static ConfigurationChangePreflight Preflight(ConfigurationChangeClass highest,
        params ConfigurationChangePreflightItem[] items) =>
        new() { HighestClass = highest, Items = items };

    private static ConfigurationChangePreflightItem Item(string label, ConfigurationChangeClass changeClass,
        string? oldValue = null, string? newValue = null, string? consequence = null) =>
        new()
        {
            Key = label.ToLowerInvariant(),
            Label = label,
            Class = changeClass,
            OldDisplayValue = oldValue,
            NewDisplayValue = newValue,
            Consequence = consequence
        };
}
