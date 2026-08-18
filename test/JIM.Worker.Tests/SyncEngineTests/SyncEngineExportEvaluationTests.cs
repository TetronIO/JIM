// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The MVO-deletion export decision, extracted from <c>ExportEvaluationServer</c> into the pure engine as the
/// first slice of the #288 outbound unbraiding (plan Phase 1a/1c). The decision under test is the one #655
/// settled: deprovisioning is driven by each matching export Synchronisation Rule's OutboundDeprovisionAction,
/// Delete wins a conflict, and the one-Pending-Export-per-CSO collision policy chooses reuse, replace or create.
/// These tests pin the extracted decision to the behaviour the braided implementation had, so the extraction is
/// provably behaviour-preserving before the orchestrator swaps over to it.
/// </summary>
[TestFixture]
public class SyncEngineExportEvaluationTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void DecideMvoDeletionExport_NoRuleMatchesTheCso_DisconnectsOnly()
    {
        var cso = Cso();
        var rules = RulesByMvoType(100, ExportRule(connectedSystemId: 99, csoTypeId: 5, OutboundDeprovisionAction.Delete));

        var decision = _engine.DecideMvoDeletionExport(cso, 100, rules, existingPendingExport: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldStageDeleteExport, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(MvoDeletionExportReason.NoMatchingExportRule));
        }
    }

    [Test]
    public void DecideMvoDeletionExport_MvoHasNoType_DisconnectsOnlyAndSaysWhy()
    {
        // An MVO with no Type cannot be matched to any export Synchronisation Rule; the braided code logged a
        // warning and disconnected only. The reason is distinct from "no matching rule" because the remedy is
        // different: fix the object, not the rules.
        var decision = _engine.DecideMvoDeletionExport(Cso(), null, RulesByMvoType(100), existingPendingExport: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldStageDeleteExport, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(MvoDeletionExportReason.NoMetaverseObjectType));
        }
    }

    [Test]
    public void DecideMvoDeletionExport_MatchingRulesAllSayDisconnect_DisconnectsOnly()
    {
        var rules = RulesByMvoType(100, ExportRule(1, 5, OutboundDeprovisionAction.Disconnect));

        var decision = _engine.DecideMvoDeletionExport(Cso(), 100, rules, existingPendingExport: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldStageDeleteExport, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(MvoDeletionExportReason.MatchingRulesDeclineDeletion));
            Assert.That(decision.MatchingRuleCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void DecideMvoDeletionExport_ADeleteRuleMatches_StagesADeleteCarryingTheSecondaryExternalId()
    {
        // The secondary external id (the DN, for LDAP) must travel on the decision: the CSO is disconnected
        // right after this and may be deleted by housekeeping before the export runs, so the connector can only
        // delete the right object if the identifier was preserved at decision time.
        var deleteRule = ExportRule(1, 5, OutboundDeprovisionAction.Delete);
        var cso = Cso(secondaryExternalId: "cn=alice,ou=People,dc=corp");

        var decision = _engine.DecideMvoDeletionExport(cso, 100, RulesByMvoType(100, deleteRule), existingPendingExport: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldStageDeleteExport, Is.True);
            Assert.That(decision.Reason, Is.EqualTo(MvoDeletionExportReason.DeleteRuleWon));
            Assert.That(decision.WinningRule, Is.SameAs(deleteRule));
            Assert.That(decision.SecondaryExternalIdValue, Is.EqualTo("cn=alice,ou=People,dc=corp"));
            Assert.That(decision.SecondaryExternalIdAttribute, Is.Not.Null);
            Assert.That(decision.ExistingPendingExportToReuse, Is.Null);
            Assert.That(decision.MustReplaceExistingPendingExport, Is.False);
        }
    }

    [Test]
    public void DecideMvoDeletionExport_RulesConflict_DeleteWinsAndTheConflictIsVisible()
    {
        // #655: Delete wins when matching rules disagree. The conflict is surfaced on the decision so the
        // orchestrator can log it; a silently-resolved conflict is how two administrators each believe their
        // rule is in charge.
        var deleteRule = ExportRule(1, 5, OutboundDeprovisionAction.Delete);
        var disconnectRule = ExportRule(1, 5, OutboundDeprovisionAction.Disconnect);

        var decision = _engine.DecideMvoDeletionExport(
            Cso(), 100, RulesByMvoType(100, disconnectRule, deleteRule), existingPendingExport: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldStageDeleteExport, Is.True);
            Assert.That(decision.WinningRule, Is.SameAs(deleteRule));
            Assert.That(decision.MatchingRuleCount, Is.EqualTo(2));
            Assert.That(decision.RulesConflicted, Is.True);
        }
    }

    [Test]
    public void DecideMvoDeletionExport_AnExistingDeletePendingExport_IsReusedNotDuplicated()
    {
        // PendingExports carries a unique index on ConnectedSystemObjectId, so a second Delete for the same CSO
        // is not merely wasteful: it is an INSERT that fails. Reuse is the collision policy.
        var existing = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Delete };

        var decision = _engine.DecideMvoDeletionExport(
            Cso(), 100, RulesByMvoType(100, ExportRule(1, 5, OutboundDeprovisionAction.Delete)), existing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldStageDeleteExport, Is.True);
            Assert.That(decision.ExistingPendingExportToReuse, Is.SameAs(existing));
            Assert.That(decision.MustReplaceExistingPendingExport, Is.False);
        }
    }

    [Test]
    public void DecideMvoDeletionExport_AnExistingPendingExportOfAnotherChangeType_IsReplaced()
    {
        var existing = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update };

        var decision = _engine.DecideMvoDeletionExport(
            Cso(), 100, RulesByMvoType(100, ExportRule(1, 5, OutboundDeprovisionAction.Delete)), existing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldStageDeleteExport, Is.True);
            Assert.That(decision.MustReplaceExistingPendingExport, Is.True);
            Assert.That(decision.ExistingPendingExportToReuse, Is.Null);
        }
    }

    [Test]
    public void DecideMvoDeletionExport_CsoWithNoSecondaryExternalId_StillStagesButCarriesNoIdentifier()
    {
        // The braided code warned and staged anyway: a delete export without the identifier may still succeed
        // while the CSO row survives, and refusing to stage would leave the object undeleted silently.
        var decision = _engine.DecideMvoDeletionExport(
            Cso(secondaryExternalId: null), 100,
            RulesByMvoType(100, ExportRule(1, 5, OutboundDeprovisionAction.Delete)), existingPendingExport: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldStageDeleteExport, Is.True);
            Assert.That(decision.SecondaryExternalIdValue, Is.Null);
            Assert.That(decision.SecondaryExternalIdAttribute, Is.Null);
        }
    }

    [Test]
    public void DecideMvoDeletionExport_RuleMatchingIsOnSystemAndObjectTypeTogether_NotSystemAlone()
    {
        // A rule for the right system but the wrong Connected System Object Type must not match; the braided
        // filter was on the (system, object type) pair and the extraction must not widen it.
        var wrongTypeRule = ExportRule(connectedSystemId: 1, csoTypeId: 6, OutboundDeprovisionAction.Delete);

        var decision = _engine.DecideMvoDeletionExport(Cso(), 100, RulesByMvoType(100, wrongTypeRule), existingPendingExport: null);

        Assert.That(decision.ShouldStageDeleteExport, Is.False);
    }

    [Test]
    public void WorkingSet_ADecisionRecordedForACso_IsReturnedOnTheSecondAsk()
    {
        // The in-run working set is what replaces reading back this run's own staged decisions from the
        // database: "what have we already decided about this CSO" becomes a dictionary hit, and a second
        // evaluation path touching the same CSO in one run cannot stage a duplicate.
        var workingSet = new ExportEvaluationWorkingSet();
        var csoId = Guid.NewGuid();
        var decision = MvoDeletionExportDecision.DisconnectOnly(MvoDeletionExportReason.NoMatchingExportRule);

        workingSet.RecordDeleteDecision(csoId, decision);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(workingSet.TryGetDeleteDecision(csoId, out var found), Is.True);
            Assert.That(found, Is.EqualTo(decision));
        }
    }

    [Test]
    public void WorkingSet_ACsoNoDecisionWasRecordedFor_ReportsNone()
    {
        Assert.That(new ExportEvaluationWorkingSet().TryGetDeleteDecision(Guid.NewGuid(), out _), Is.False);
    }

    private static ConnectedSystemObject Cso(string? secondaryExternalId = "cn=test,dc=corp")
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            TypeId = 5
        };

        if (secondaryExternalId != null)
        {
            const int secondaryIdAttributeId = 42;
            cso.SecondaryExternalIdAttributeId = secondaryIdAttributeId;
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = secondaryIdAttributeId,
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = secondaryIdAttributeId, Name = "dn" },
                StringValue = secondaryExternalId
            });
        }

        return cso;
    }

    private static SyncRule ExportRule(int connectedSystemId, int csoTypeId, OutboundDeprovisionAction action) => new()
    {
        Name = $"Export rule ({action})",
        ConnectedSystemId = connectedSystemId,
        ConnectedSystemObjectTypeId = csoTypeId,
        MetaverseObjectTypeId = 100,
        Direction = SyncRuleDirection.Export,
        Enabled = true,
        OutboundDeprovisionAction = action
    };

    private static Dictionary<int, List<SyncRule>> RulesByMvoType(int mvoTypeId, params SyncRule[] rules) =>
        rules.Length == 0 ? new Dictionary<int, List<SyncRule>>() : new Dictionary<int, List<SyncRule>> { [mvoTypeId] = rules.ToList() };
}
