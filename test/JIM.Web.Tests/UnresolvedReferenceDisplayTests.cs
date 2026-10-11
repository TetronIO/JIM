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
