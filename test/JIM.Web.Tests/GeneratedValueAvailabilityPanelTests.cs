// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Logic;
using JIM.Web.Pages.Admin.Components;
using JIM.Web.Shared;
using MudBlazor;
using MudBlazor.Extensions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// "Checked for availability in" on the generated Attribute Flow form (Unique Value Generation, #242, release 3): which
/// rows, switches and reason lines show when, and that the Include switch edits the generation's exclusions in place
/// and tells the host, which reloads the rows and saves the exclusions with the other generation settings.
/// </summary>
[TestFixture]
public class GeneratedValueAvailabilityPanelTests : JimComponentTestContext
{
    private const string RowMarker = "[data-testid='generated-value-availability-row']";

    [TearDown]
    public async Task TearDownAsync() => await DisposeComponentsAsync();

    private static GeneratedValueParticipant Row(int systemId, string systemName, GeneratedValueParticipantCheck check, GeneratedValueParticipantReason reason,
        bool canBeExcluded = true, bool isExcluded = false, bool reportsCollisions = false) => new()
    {
        ConnectedSystemId = systemId,
        ConnectedSystemName = systemName,
        ConnectorName = "JIM LDAP Connector",
        ConnectedSystemObjectTypeAttributeId = systemId * 10,
        AttributeName = "sAMAccountName",
        Check = check,
        Reason = reason,
        CanBeExcluded = canBeExcluded,
        IsExcluded = isExcluded,
        ReportsCollisions = reportsCollisions
    };

    private IRenderedComponent<GeneratedValueAvailabilityPanel> RenderPanel(IReadOnlyList<GeneratedValueParticipant>? participants, SyncRuleMappingGeneration generation, Action? onChanged = null) =>
        Render<GeneratedValueAvailabilityPanel>(ps => ps
            .Add(c => c.Participants, participants)
            .Add(c => c.Generation, generation)
            .Add(c => c.OnChanged, () => onChanged?.Invoke()));

    [Test]
    public void Render_WhileLoading_ShowsNoRowsAndNoEmptyText()
    {
        var cut = RenderPanel(null, new SyncRuleMappingGeneration());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(RowMarker), Is.Empty);
            Assert.That(cut.FindAll("[data-testid='generated-value-availability-empty']"), Is.Empty);
            Assert.That(cut.HasComponent<MudProgressLinear>(), Is.True);
        }
    }

    [Test]
    public void Render_NoParticipants_SaysSoInsteadOfATable()
    {
        var cut = RenderPanel([], new SyncRuleMappingGeneration());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("[data-testid='generated-value-availability-empty']"), Has.Count.EqualTo(1));
            Assert.That(cut.HasComponent<MudSimpleTable>(), Is.False);
        }
    }

    [Test]
    public void Render_Participants_ShowsOneRowEachInOrder()
    {
        var cut = RenderPanel(
        [
            Row(2, "Active Directory", GeneratedValueParticipantCheck.JimRecordsAndProbe, GeneratedValueParticipantReason.None),
            Row(3, "Payroll", GeneratedValueParticipantCheck.JimRecordsOnly, GeneratedValueParticipantReason.ConnectorCannotProbe)
        ], new SyncRuleMappingGeneration());

        var chips = cut.FindComponents<ObjectChip>().Select(c => c.Instance.Name).ToList();
        Assert.That(chips, Is.EqualTo(new[] { "Active Directory", "Payroll" }));
    }

    [Test]
    public void Render_FullyCheckedRow_HasNoReasonLine_OtherRowsDo()
    {
        var cut = RenderPanel(
        [
            Row(2, "Active Directory", GeneratedValueParticipantCheck.JimRecordsAndProbe, GeneratedValueParticipantReason.None),
            Row(3, "Payroll", GeneratedValueParticipantCheck.JimRecordsOnly, GeneratedValueParticipantReason.ConnectorCannotProbe)
        ], new SyncRuleMappingGeneration());

        var rows = cut.FindAll(RowMarker);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows[0].QuerySelectorAll("[data-testid='generated-value-availability-reason']"), Is.Empty);
            Assert.That(rows[1].QuerySelectorAll("[data-testid='generated-value-availability-reason']"), Has.Length.EqualTo(1));
        }
    }

    [Test]
    public void Render_RowThatCannotBeExcluded_HasADashNotASwitch()
    {
        var cut = RenderPanel(
        [
            Row(2, "Active Directory", GeneratedValueParticipantCheck.JimRecordsAndProbe, GeneratedValueParticipantReason.None),
            Row(4, "Mail", GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.ExportedThroughExpression, canBeExcluded: false)
        ], new SyncRuleMappingGeneration());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindComponents<MudSwitch<bool>>(), Has.Count.EqualTo(1));
            Assert.That(cut.FindComponents<EmptyValue>(), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Render_ExcludedSystem_IsDimmedWithItsSwitchOff()
    {
        var generation = new SyncRuleMappingGeneration();
        generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = 2 });

        var cut = RenderPanel([Row(2, "Active Directory", GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.Excluded, isExcluded: true)], generation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Find(RowMarker).GetAttribute("data-excluded"), Is.EqualTo("true"));
            Assert.That(cut.FindComponent<MudSwitch<bool>>().Instance.GetState(x => x.Value), Is.False);
        }
    }

    [Test]
    public async Task IncludeSwitchedOff_AddsTheExclusionAndTellsTheHostAsync()
    {
        var generation = new SyncRuleMappingGeneration { Id = 7 };
        var changes = 0;
        var cut = RenderPanel([Row(2, "Active Directory", GeneratedValueParticipantCheck.JimRecordsAndProbe, GeneratedValueParticipantReason.None)], generation, () => changes++);

        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generation.Exclusions.Select(e => e.ConnectedSystemId), Is.EqualTo(new[] { 2 }));
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(cut.Find(RowMarker).GetAttribute("data-excluded"), Is.EqualTo("true"), "the row follows the form before the host reloads");
        }
    }

    [Test]
    public async Task IncludeSwitchedBackOn_RemovesTheExclusionAsync()
    {
        var generation = new SyncRuleMappingGeneration();
        generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = 2 });
        var changes = 0;
        var cut = RenderPanel([Row(2, "Active Directory", GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.Excluded, isExcluded: true)], generation, () => changes++);

        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generation.Exclusions, Is.Empty);
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(cut.Find(RowMarker).QuerySelectorAll("[data-testid='generated-value-availability-reason']"), Is.Empty,
                "until the host reloads, the row no longer claims an exclusion it does not have");
        }
    }
    [Test]
    public void Render_ReportsCollisions_SaysWhichSystemsCanAndExplainsOnlyThoseThatCannot()
    {
        var cut = RenderPanel(
        [
            Row(2, "Active Directory", GeneratedValueParticipantCheck.JimRecordsAndProbe, GeneratedValueParticipantReason.None, reportsCollisions: true),
            Row(3, "Payroll", GeneratedValueParticipantCheck.JimRecordsOnly, GeneratedValueParticipantReason.ConnectorCannotProbe)
        ], new SyncRuleMappingGeneration());

        var rows = cut.FindAll(RowMarker);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows[0].QuerySelector("[data-testid='generated-value-availability-reports-collisions']")!.GetAttribute("data-reports"), Is.EqualTo("true"));
            Assert.That(rows[1].QuerySelector("[data-testid='generated-value-availability-reports-collisions']")!.GetAttribute("data-reports"), Is.EqualTo("false"));
            Assert.That(rows[0].QuerySelectorAll("[data-testid='generated-value-availability-collision-note']"), Is.Empty);
            Assert.That(rows[1].QuerySelectorAll("[data-testid='generated-value-availability-collision-note']"), Has.Length.EqualTo(1));
        }
    }
}
