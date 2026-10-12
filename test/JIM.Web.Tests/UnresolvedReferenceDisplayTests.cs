// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How the Unresolved References section presents itself (#959): red where the run counted the references as the
/// item's error, amber where it left them unresolved without doing so, what has happened to them since the run, and
/// where each likely cause sends the administrator to fix it.
/// </summary>
[TestFixture]
public class UnresolvedReferenceDisplayTests
{
    private const int ConnectedSystemId = 7;

    [TestCase(3)]
    [TestCase(null)]
    public void ToneFor_UnresolvedReferenceErrorHoldsTheSlot_IsError(int? count)
    {
        // Null is an item recorded before the count existed: its error still says references went unresolved.
        Assert.That(UnresolvedReferenceDisplay.ToneFor(ActivityRunProfileExecutionItemErrorType.UnresolvedReference, count),
            Is.EqualTo(UnresolvedReferenceTone.Error));
    }

    [TestCase(null)]
    [TestCase(ActivityRunProfileExecutionItemErrorType.NotSet)]
    [TestCase(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError)]
    public void ToneFor_ReferencesLeftUnresolvedWithoutBeingTheError_IsWarning(ActivityRunProfileExecutionItemErrorType? errorType)
    {
        // Warn or Ignore handling, or another error holding the item's one error slot.
        Assert.That(UnresolvedReferenceDisplay.ToneFor(errorType, 2), Is.EqualTo(UnresolvedReferenceTone.Warning));
    }

    [TestCase(null)]
    [TestCase(0)]
    public void ToneFor_NothingLeftUnresolved_IsNone(int? count)
    {
        Assert.That(UnresolvedReferenceDisplay.ToneFor(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError, count),
            Is.EqualTo(UnresolvedReferenceTone.None));
    }

    [TestCase(3, 3, UnresolvedReferenceSinceRun.Unchanged)]
    [TestCase(3, 2, UnresolvedReferenceSinceRun.Fewer)]
    [TestCase(3, 5, UnresolvedReferenceSinceRun.More)]
    [TestCase(3, 0, UnresolvedReferenceSinceRun.NoneNow)]
    [TestCase(null, 0, UnresolvedReferenceSinceRun.NoneNow)]
    [TestCase(null, 4, UnresolvedReferenceSinceRun.RunCountUnknown)]
    public void SinceRun_ObjectStillExists_ComparesTheRunWithNow(int? runCount, int nowCount, UnresolvedReferenceSinceRun expected)
    {
        Assert.That(UnresolvedReferenceDisplay.SinceRun(runCount, nowCount), Is.EqualTo(expected));
    }

    [TestCase(UnresolvedReferenceCause.ContainerNotInScope, "?t=scope")]
    [TestCase(UnresolvedReferenceCause.ContainerExcluded, "?t=scope")]
    [TestCase(UnresolvedReferenceCause.PartitionNotSelected, "?t=scope")]
    [TestCase(UnresolvedReferenceCause.AmbiguousObjectType, "?t=schema&ot=group")]
    [TestCase(UnresolvedReferenceCause.OutsideConnectedSystem, "?t=settings")]
    [TestCase(UnresolvedReferenceCause.NotFoundInScope, "?t=run-profiles")]
    [TestCase(UnresolvedReferenceCause.NotFound, "?t=run-profiles")]
    public void ActionFor_EachCause_OpensTheTabWhereItIsFixed(UnresolvedReferenceCause cause, string expectedQuery)
    {
        var action = UnresolvedReferenceDisplay.ActionFor(cause, ConnectedSystemId, "group");

        Assert.That(action.Href, Is.EqualTo($"/admin/connected-systems/{ConnectedSystemId}{expectedQuery}"));
    }

    [Test]
    public void ActionFor_ObjectTypeNameNeedingEscaping_IsEscapedInTheLink()
    {
        var action = UnresolvedReferenceDisplay.ActionFor(UnresolvedReferenceCause.AmbiguousObjectType, ConnectedSystemId, "HR & Payroll");

        Assert.That(action.Href, Does.EndWith("&ot=HR%20%26%20Payroll"));
    }

    [Test]
    public void ActionFor_ObjectTypeUnknown_OpensTheSchemaWithoutChoosingAType()
    {
        var action = UnresolvedReferenceDisplay.ActionFor(UnresolvedReferenceCause.AmbiguousObjectType, ConnectedSystemId, null);

        Assert.That(action.Href, Is.EqualTo($"/admin/connected-systems/{ConnectedSystemId}?t=schema"));
    }

    [TestCase("OU=Contractors,DC=corp is not in Container Scope.", "OU=Contractors,DC=corp", "", " is not in Container Scope.")]
    [TestCase("Partition DC=lab,DC=corp is not selected.", "DC=lab,DC=corp", "Partition ", " is not selected.")]
    public void SplitFinding_FindingNamingTheIdentifier_SetsItApart(string finding, string identifier, string before, string after)
    {
        var parts = UnresolvedReferenceDisplay.SplitFinding(finding, identifier);

        Assert.That(parts, Is.EqualTo((before, (string?)identifier, after)));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("OU=Elsewhere,DC=corp")]
    public void SplitFinding_NoIdentifierInTheFinding_LeavesItWhole(string? identifier)
    {
        const string finding = "This value is in none of Corp AD's partitions.";

        Assert.That(UnresolvedReferenceDisplay.SplitFinding(finding, identifier), Is.EqualTo((finding, (string?)null, "")));
    }

    [Test]
    public void GroupFixes_StepsForBothPlaces_GroupsThemJimFirstKeepingTheirOrder()
    {
        UnresolvedReferenceFix Fix(UnresolvedReferenceFixLocation location, int count) =>
            new() { FixLocation = location, Count = count, Finding = "f", Remedy = "r" };

        var groups = UnresolvedReferenceDisplay.GroupFixes(
        [
            Fix(UnresolvedReferenceFixLocation.SourceSystem, 9),
            Fix(UnresolvedReferenceFixLocation.Jim, 5),
            Fix(UnresolvedReferenceFixLocation.SourceSystem, 2),
            Fix(UnresolvedReferenceFixLocation.Jim, 1)
        ]);

        Assert.That(groups.Select(g => (g.FixLocation, g.Fixes.Select(f => f.Count).ToArray())), Is.EqualTo(new[]
        {
            (UnresolvedReferenceFixLocation.Jim, new[] { 5, 1 }),
            (UnresolvedReferenceFixLocation.SourceSystem, new[] { 9, 2 })
        }), "What the administrator can change in JIM comes first, whatever the counts.");
    }

    [TestCase(UnresolvedReferenceHandling.Error, UnresolvedReferenceTone.Warning)]
    [TestCase(UnresolvedReferenceHandling.Warn, UnresolvedReferenceTone.Warning)]
    [TestCase(UnresolvedReferenceHandling.Ignore, UnresolvedReferenceTone.None)]
    public void SignpostTone_Handling_IsAmberOnlyWhereTheAdministratorAskedToBeTold(UnresolvedReferenceHandling handling, UnresolvedReferenceTone expected)
    {
        Assert.That(UnresolvedReferenceDisplay.SignpostTone(handling), Is.EqualTo(expected));
    }

    [Test]
    public void SignpostTooltip_ReferencesOnSeveralObjects_SaysHowManyOfEachAndTheHandling()
    {
        var tooltip = UnresolvedReferenceDisplay.SignpostTooltip(
            new ConnectedSystemUnresolvedReferenceTotals { ReferenceCount = 1284, ObjectCount = 312 }, UnresolvedReferenceHandling.Warn);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tooltip, Does.Contain("1,284 references"));
            Assert.That(tooltip, Does.Contain("312 Connected System Objects"));
            Assert.That(tooltip, Does.Contain("Warn"));
        }
    }

    [Test]
    public void SignpostTooltip_OneReferenceOnOneObject_IsSingular()
    {
        var tooltip = UnresolvedReferenceDisplay.SignpostTooltip(
            new ConnectedSystemUnresolvedReferenceTotals { ReferenceCount = 1, ObjectCount = 1 }, UnresolvedReferenceHandling.Error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tooltip, Does.Contain("1 reference "));
            Assert.That(tooltip, Does.Contain("1 Connected System Object "));
        }
    }

    [Test]
    public void SignpostTooltip_Ignore_SaysRunsDoNotReportThem()
    {
        var warn = UnresolvedReferenceDisplay.SignpostTooltip(new ConnectedSystemUnresolvedReferenceTotals { ReferenceCount = 2, ObjectCount = 1 }, UnresolvedReferenceHandling.Warn);
        var ignore = UnresolvedReferenceDisplay.SignpostTooltip(new ConnectedSystemUnresolvedReferenceTotals { ReferenceCount = 2, ObjectCount = 1 }, UnresolvedReferenceHandling.Ignore);

        Assert.That(ignore, Has.Length.GreaterThan(warn.Length),
            "Ignore stops runs reporting them, which the button's neutral colour alone does not explain.");
    }

    [Test]
    public void ConnectorSpaceHref_ConnectedSystem_OpensItsConnectorSpaceFilteredToUnresolvedReferences()
    {
        var href = UnresolvedReferenceDisplay.ConnectorSpaceHref(ConnectedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(href, Is.EqualTo($"/admin/connected-systems/{ConnectedSystemId}/connector-space?references={UnresolvedReferenceDisplay.UnresolvedReferencesFilterValue}"));
            Assert.That(UnresolvedReferenceDisplay.IsUnresolvedReferencesFilter(UnresolvedReferenceDisplay.UnresolvedReferencesFilterValue), Is.True,
                "The Connector Space must recognise the value the link carries.");
        }
    }

    [TestCase("unresolved", true)]
    [TestCase("UNRESOLVED", true)]
    [TestCase("resolved", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsUnresolvedReferencesFilter_QueryValue_IsRecognisedIgnoringCase(string? value, bool expected)
    {
        Assert.That(UnresolvedReferenceDisplay.IsUnresolvedReferencesFilter(value), Is.EqualTo(expected));
    }

    [TestCase(1, "on 1 object")]
    [TestCase(241, "on 241 objects")]
    [TestCase(12034, "on 12,034 objects")]
    public void ObjectCountText_Count_IsPluralisedAndGrouped(int count, string expected)
    {
        Assert.That(UnresolvedReferenceDisplay.ObjectCountText(count), Is.EqualTo(expected));
    }

    [Test]
    public void CauseLabel_EveryCause_HasItsOwnLabel()
    {
        var labels = Enum.GetValues<UnresolvedReferenceCause>().Select(UnresolvedReferenceDisplay.CauseLabel).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(labels, Has.All.Not.Empty);
            Assert.That(labels, Is.Unique, "Two causes sharing a label would read as one in the chips and the table.");
        }
    }
}
