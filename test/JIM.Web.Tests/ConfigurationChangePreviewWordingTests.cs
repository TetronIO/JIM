// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How the shared preview panel words and orders what it found, where that depends on what was found (#134). A deletion
/// preview reports contributor takeovers, where the Connected System on a row is where the value now comes FROM rather
/// than where the objects are, and values whose meaning depends on which system contributed them.
/// </summary>
[TestFixture]
public class ConfigurationChangePreviewWordingTests
{
    private const string DeletedSystem = "Old HR System";

    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue)]
    public void SystemClause_Takeover_NamesTheSystemAsTheNewSource(ActivityRunProfileExecutionItemSyncOutcomeType transition)
    {
        Assert.That(ConfigurationChangePreviewWording.SystemClause(transition, "New HR System"), Is.EqualTo(", now from New HR System"));
    }

    [Test]
    public void SystemClause_AnyOtherTransition_NamesWhereTheObjectsAre()
    {
        Assert.That(ConfigurationChangePreviewWording.SystemClause(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport, "Active Directory"),
            Is.EqualTo(" in Active Directory"));
    }

    [Test]
    public void SystemClause_NoSystem_SaysNothing()
    {
        Assert.That(ConfigurationChangePreviewWording.SystemClause(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, null), Is.Empty);
    }

    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue)]
    public void ValueContributor_DeletionPreviewCurrentValue_IsTheSystemBeingDeleted(ActivityRunProfileExecutionItemSyncOutcomeType transition)
    {
        var contributor = ConfigurationChangePreviewWording.ValueContributor(
            ConfigurationChangePreviewSurface.ConnectedSystemDeletion, transition, proposedValue: false, DeletedSystem, groupSystemName: "New HR System");

        Assert.That(contributor, Is.EqualTo(DeletedSystem));
    }

    [Test]
    public void ValueContributor_DeletionPreviewTakeoverProposedValue_IsTheNewContributor()
    {
        var contributor = ConfigurationChangePreviewWording.ValueContributor(
            ConfigurationChangePreviewSurface.ConnectedSystemDeletion, ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue,
            proposedValue: true, DeletedSystem, groupSystemName: "New HR System");

        Assert.That(contributor, Is.EqualTo("New HR System"));
    }

    [Test]
    public void ValueContributor_DeletionPreviewExportRow_NamesNone()
    {
        // A target's values are the target's own; no contributor applies to them.
        var contributor = ConfigurationChangePreviewWording.ValueContributor(
            ConfigurationChangePreviewSurface.ConnectedSystemDeletion, ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport,
            proposedValue: false, DeletedSystem, groupSystemName: "Active Directory");

        Assert.That(contributor, Is.Null);
    }

    [Test]
    public void ValueContributor_AnyOtherSurface_NamesNone()
    {
        var contributor = ConfigurationChangePreviewWording.ValueContributor(
            ConfigurationChangePreviewSurface.SynchronisationRuleAttributeFlow, ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor,
            proposedValue: false, "HR Import", groupSystemName: "HR");

        Assert.That(contributor, Is.Null);
    }

    [Test]
    public void NothingWouldChange_DeletionPreview_SaysTheSystemsOwnObjectsStillGo()
    {
        // Generic "this change would not change anything" is false for a deletion: the system's own objects go with it.
        Assert.That(ConfigurationChangePreviewWording.NothingWouldChange(ConfigurationChangePreviewSurface.ConnectedSystemDeletion),
            Is.Not.EqualTo(ConfigurationChangePreviewWording.NothingWouldChange(ConfigurationChangePreviewSurface.MetaverseObjectType)));
    }

    [Test]
    public void ByConsequence_OrdersByTheWeightOfTheConsequenceThenSizeWithQuietGroupsLast()
    {
        // A grid sorted by size alone would put 11,950 identical-value takeovers above 37 deprovisions.
        var groups = new List<ConfigurationChangePreviewGroup>
        {
            Group(ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue, 11_950),
            Group(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport, 800),
            Group(ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, 650),
            Group(ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, 37),
            Group(ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue, 800),
            Group(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 312)
        };

        var ordered = ConfigurationChangePreviewWording.ByConsequence(groups).Select(g => (g.TransitionType, g.ObjectCount));

        Assert.That(ordered, Is.EqualTo(new[]
        {
            (ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 312),
            (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport, 37),
            (ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue, 800),
            (ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor, 650),
            (ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageUpdateExport, 800),
            (ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue, 11_950)
        }));
    }

    private static ConfigurationChangePreviewGroup Group(ActivityRunProfileExecutionItemSyncOutcomeType transition, int objects) =>
        new() { TransitionType = transition, ObjectCount = objects };
}
