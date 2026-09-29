// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Logic;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// A queued Pending Export change must not outlive the reason it was queued: before an export runs, a queued change
/// attributed to an export Synchronisation Rule is withdrawn when the rule no longer exists or is disabled, when the
/// rule no longer has an enabled Attribute Flow for the change's attribute, or when the object's account is no longer
/// joined (it left the rule's scope with the Disconnect action). Class membership changes have no Attribute Flow of
/// their own; they are withdrawn with the rule, the join, or the attribute changes they were planned for.
/// </summary>
[TestFixture]
public class SyncEngineQueuedChangeAuthorityTests
{
    private const int DisplayNameId = 10;
    private const int DepartmentId = 11;
    private const int ObjectClassId = 12;

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void SelectQueuedChangesWithoutAuthority_EveryChangeStillFlowed_SelectsNothing()
    {
        var rule = ExportRule(1, DisplayNameId, DepartmentId);
        var pe = UpdateExport(Change(DisplayNameId, rule.Id), Change(DepartmentId, rule.Id));

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, true, Rules(rule), NoClassAttributes);

        Assert.That(withdrawn, Is.Empty);
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_AttributeFlowRemoved_SelectsOnlyThatAttributesChange()
    {
        var rule = ExportRule(1, DepartmentId);
        var displayName = Change(DisplayNameId, rule.Id);
        var pe = UpdateExport(displayName, Change(DepartmentId, rule.Id));

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, true, Rules(rule), NoClassAttributes);

        Assert.That(withdrawn, Is.EqualTo(new[] { displayName }));
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_AttributeFlowDisabled_SelectsThatAttributesChange()
    {
        var rule = ExportRule(1, DisplayNameId);
        rule.AttributeFlowRules.Single().Enabled = false;
        var displayName = Change(DisplayNameId, rule.Id);

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(UpdateExport(displayName), true, Rules(rule), NoClassAttributes);

        Assert.That(withdrawn, Is.EqualTo(new[] { displayName }));
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_RuleDisabled_SelectsEveryChangeItQueued()
    {
        var rule = ExportRule(1, DisplayNameId, DepartmentId);
        rule.Enabled = false;
        var pe = UpdateExport(Change(DisplayNameId, rule.Id), Change(DepartmentId, rule.Id));

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, true, Rules(rule), NoClassAttributes);

        Assert.That(withdrawn, Is.EquivalentTo(pe.AttributeValueChanges));
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_RuleDeleted_SelectsEveryChangeItQueued()
    {
        var pe = UpdateExport(Change(DisplayNameId, syncRuleId: 99));

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, true, Rules(ExportRule(1, DisplayNameId)), NoClassAttributes);

        Assert.That(withdrawn, Is.EquivalentTo(pe.AttributeValueChanges));
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_AccountNoLongerJoined_SelectsEveryRuleAttributedChange()
    {
        var rule = ExportRule(1, DisplayNameId);
        var pe = UpdateExport(Change(DisplayNameId, rule.Id));

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, false, Rules(rule), NoClassAttributes);

        Assert.That(withdrawn, Is.EquivalentTo(pe.AttributeValueChanges));
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_ChangeWithNoRuleAttribution_IsNeverSelected()
    {
        // No attribution means no rule to test the change against; it is left exactly as before.
        var pe = UpdateExport(Change(DisplayNameId, syncRuleId: null));

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, false, Rules(), NoClassAttributes);

        Assert.That(withdrawn, Is.Empty);
    }

    [TestCase(PendingExportAttributeChangeStatus.ExportedPendingConfirmation)]
    [TestCase(PendingExportAttributeChangeStatus.Failed)]
    public void SelectQueuedChangesWithoutAuthority_ChangeNotAwaitingSend_IsNeverSelected(PendingExportAttributeChangeStatus status)
    {
        // A change already sent awaits its confirming import; a failed one is not resent. Neither is queued.
        var change = Change(DisplayNameId, 1, status);

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(UpdateExport(change), true, Rules(ExportRule(1)), NoClassAttributes);

        Assert.That(withdrawn, Is.Empty);
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_ExportedNotConfirmedChangeWithoutAuthority_IsSelected()
    {
        // A change whose send was not confirmed is queued for a resend, so it is as stale as a Pending one.
        var change = Change(DisplayNameId, 1, PendingExportAttributeChangeStatus.ExportedNotConfirmed);

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(UpdateExport(change), true, Rules(ExportRule(1)), NoClassAttributes);

        Assert.That(withdrawn, Is.EqualTo(new[] { change }));
    }

    [TestCase(PendingExportChangeType.Create)]
    [TestCase(PendingExportChangeType.Delete)]
    public void SelectQueuedChangesWithoutAuthority_NotAnUpdate_SelectsNothing(PendingExportChangeType changeType)
    {
        // A Create's attributes are one provisioning unit and a Delete's carry the object's identity; neither is trimmed.
        var pe = UpdateExport(Change(DisplayNameId, 1));
        pe.ChangeType = changeType;

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, false, Rules(), NoClassAttributes);

        Assert.That(withdrawn, Is.Empty);
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_ExportExecuting_SelectsNothing()
    {
        var pe = UpdateExport(Change(DisplayNameId, 1));
        pe.Status = PendingExportStatus.Executing;

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, false, Rules(), NoClassAttributes);

        Assert.That(withdrawn, Is.Empty);
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_ClassMembershipWithItsAttributesStillFlowed_IsKept()
    {
        // Class membership is JIM-computed rather than flowed, so having no Attribute Flow of its own is normal.
        var rule = ExportRule(1, DisplayNameId);
        var pe = UpdateExport(Change(DisplayNameId, rule.Id), Change(ObjectClassId, rule.Id, changeType: PendingExportAttributeChangeType.Add));

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, true, Rules(rule), ClassAttributes);

        Assert.That(withdrawn, Is.Empty);
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_ClassMembershipPlannedForAWithdrawnChange_IsWithdrawnWithIt()
    {
        // The classes were planned for the attributes being written; with one of those withdrawn the plan no longer
        // holds, and an added class whose required attribute is not written would be refused. Next evaluation replans.
        var rule = ExportRule(1, DepartmentId);
        var displayName = Change(DisplayNameId, rule.Id);
        var objectClass = Change(ObjectClassId, rule.Id, changeType: PendingExportAttributeChangeType.Add);
        var pe = UpdateExport(displayName, Change(DepartmentId, rule.Id), objectClass);

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, true, Rules(rule), ClassAttributes);

        Assert.That(withdrawn, Is.EquivalentTo(new[] { displayName, objectClass }));
    }

    [Test]
    public void SelectQueuedChangesWithoutAuthority_AnotherRulesChangeWithdrawn_KeepsThisRulesClassMembership()
    {
        var keptRule = ExportRule(1, DepartmentId);
        var staleRule = ExportRule(2);
        var stale = Change(DisplayNameId, staleRule.Id);
        var pe = UpdateExport(stale, Change(DepartmentId, keptRule.Id), Change(ObjectClassId, keptRule.Id, changeType: PendingExportAttributeChangeType.Add));

        var withdrawn = _engine.SelectQueuedChangesWithoutAuthority(pe, true, Rules(keptRule, staleRule), ClassAttributes);

        Assert.That(withdrawn, Is.EqualTo(new[] { stale }));
    }

    private static readonly IReadOnlySet<int> NoClassAttributes = new HashSet<int>();
    private static readonly IReadOnlySet<int> ClassAttributes = new HashSet<int> { ObjectClassId };

    private static SyncRule ExportRule(int id, params int[] mappedAttributeIds)
    {
        var rule = new SyncRule { Id = id, Name = $"Rule {id}", Enabled = true, Direction = SyncRuleDirection.Export };
        foreach (var attributeId in mappedAttributeIds)
            rule.AttributeFlowRules.Add(new SyncRuleMapping { SyncRuleId = id, TargetConnectedSystemAttributeId = attributeId });
        return rule;
    }

    private static Dictionary<int, SyncRule> Rules(params SyncRule[] rules) => rules.ToDictionary(r => r.Id);

    private static PendingExportAttributeValueChange Change(
        int attributeId,
        int? syncRuleId,
        PendingExportAttributeChangeStatus status = PendingExportAttributeChangeStatus.Pending,
        PendingExportAttributeChangeType changeType = PendingExportAttributeChangeType.Update) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = attributeId,
        SyncRuleId = syncRuleId,
        Status = status,
        ChangeType = changeType,
        StringValue = "value"
    };

    private static PendingExport UpdateExport(params PendingExportAttributeValueChange[] changes) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = 1,
        ConnectedSystemObjectId = Guid.NewGuid(),
        ChangeType = PendingExportChangeType.Update,
        Status = PendingExportStatus.Pending,
        AttributeValueChanges = changes.ToList()
    };
}
