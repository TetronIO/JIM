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
/// The out-of-scope deprovisioning family, extracted from <c>ExportEvaluationServer</c> into the pure engine as
/// the second slice of the #288 outbound unbraiding (plan Phase 1a/1c). Two decisions are under test: what an
/// export Synchronisation Rule's OutboundDeprovisionAction means for a CSO that has fallen out of the rule's
/// scope (disconnect, stage a Delete export with the one-Pending-Export-per-CSO collision policy, or nothing for
/// an unrecognised action), and whether a disconnect that removed a Projected MVO's last connector should stamp
/// LastConnectorDisconnectedDate for the deletion grace period. These pin the extracted decisions to the braided
/// implementation's behaviour so the orchestrator swap is provably behaviour-preserving.
/// </summary>
[TestFixture]
public class SyncEngineOutOfScopeDeprovisioningTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void DecideOutOfScopeDeprovisioning_RuleSaysDisconnect_DisconnectsWithoutStaging()
    {
        var decision = _engine.DecideOutOfScopeDeprovisioning(
            ExportRule(OutboundDeprovisionAction.Disconnect), existingPendingExport: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Action, Is.EqualTo(OutOfScopeDeprovisioningAction.Disconnect));
            Assert.That(decision.ExistingPendingExportToReuse, Is.Null);
            Assert.That(decision.MustReplaceExistingPendingExport, Is.False);
        }
    }

    [Test]
    public void DecideOutOfScopeDeprovisioning_RuleSaysDelete_StagesADeleteExport()
    {
        var decision = _engine.DecideOutOfScopeDeprovisioning(
            ExportRule(OutboundDeprovisionAction.Delete), existingPendingExport: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Action, Is.EqualTo(OutOfScopeDeprovisioningAction.StageDeleteExport));
            Assert.That(decision.ExistingPendingExportToReuse, Is.Null);
            Assert.That(decision.MustReplaceExistingPendingExport, Is.False);
        }
    }

    [Test]
    public void DecideOutOfScopeDeprovisioning_AnExistingDeletePendingExport_IsReusedNotDuplicated()
    {
        // PendingExports carries a unique index on ConnectedSystemObjectId, so a second Delete for the same CSO
        // is an INSERT that fails; the braided EnsureDeletePendingExportAsync reused it, and so must this.
        var existing = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Delete };

        var decision = _engine.DecideOutOfScopeDeprovisioning(
            ExportRule(OutboundDeprovisionAction.Delete), existing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Action, Is.EqualTo(OutOfScopeDeprovisioningAction.StageDeleteExport));
            Assert.That(decision.ExistingPendingExportToReuse, Is.SameAs(existing));
            Assert.That(decision.MustReplaceExistingPendingExport, Is.False);
        }
    }

    [Test]
    public void DecideOutOfScopeDeprovisioning_AnExistingPendingExportOfAnotherChangeType_IsReplaced()
    {
        var existing = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update };

        var decision = _engine.DecideOutOfScopeDeprovisioning(
            ExportRule(OutboundDeprovisionAction.Delete), existing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.Action, Is.EqualTo(OutOfScopeDeprovisioningAction.StageDeleteExport));
            Assert.That(decision.ExistingPendingExportToReuse, Is.Null);
            Assert.That(decision.MustReplaceExistingPendingExport, Is.True);
        }
    }

    [Test]
    public void DecideOutOfScopeDeprovisioning_AnUnrecognisedAction_DoesNothingAndSaysSo()
    {
        // The braided switch logged a warning and did nothing for an action it did not recognise (a future
        // enum value such as the post-MVP Disable). That must stay a visible non-action, never a default-to-
        // disconnect: deprovisioning semantics are not something to guess at.
        var decision = _engine.DecideOutOfScopeDeprovisioning(
            ExportRule((OutboundDeprovisionAction)99), existingPendingExport: null);

        Assert.That(decision.Action, Is.EqualTo(OutOfScopeDeprovisioningAction.UnknownAction));
    }

    [Test]
    public void ShouldMarkLastConnectorDisconnected_LastConnectorOfAProjectedMvoWithTheDeletionRule_SaysYes()
    {
        // The orchestrator asks AFTER removing the disconnected CSO from the MVO's collection, so the
        // no-more-connectors state is the collection being empty.
        var mvo = Mvo(remainingCsoCount: 0, MetaverseObjectOrigin.Projected,
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);

        Assert.That(_engine.ShouldMarkLastConnectorDisconnected(mvo), Is.True);
    }

    [Test]
    public void ShouldMarkLastConnectorDisconnected_OtherConnectorsRemain_SaysNo()
    {
        var mvo = Mvo(remainingCsoCount: 1, MetaverseObjectOrigin.Projected,
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);

        Assert.That(_engine.ShouldMarkLastConnectorDisconnected(mvo), Is.False);
    }

    [Test]
    public void ShouldMarkLastConnectorDisconnected_MvoWasNotProjected_SaysNo()
    {
        // An internally created MVO is not subject to automatic deletion when its connectors disconnect.
        var mvo = Mvo(remainingCsoCount: 0, MetaverseObjectOrigin.Internal,
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);

        Assert.That(_engine.ShouldMarkLastConnectorDisconnected(mvo), Is.False);
    }

    [Test]
    public void ShouldMarkLastConnectorDisconnected_TypeDeletionRuleIsManual_SaysNo()
    {
        var mvo = Mvo(remainingCsoCount: 0, MetaverseObjectOrigin.Projected, MetaverseObjectDeletionRule.Manual);

        Assert.That(_engine.ShouldMarkLastConnectorDisconnected(mvo), Is.False);
    }

    [Test]
    public void ShouldMarkLastConnectorDisconnected_MvoHasNoType_SaysNo()
    {
        var mvo = Mvo(remainingCsoCount: 0, MetaverseObjectOrigin.Projected,
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        // Type is declared non-nullable but is null-checked throughout the sync path (= null! default);
        // the braided code guarded it with ?. and the extraction must too.
        mvo.Type = null!;

        Assert.That(_engine.ShouldMarkLastConnectorDisconnected(mvo), Is.False);
    }

    [Test]
    public void WorkingSet_AStagedDeleteExportRecordedForACso_IsReturnedOnTheSecondAsk()
    {
        // What completes Phase 1a: a Delete Pending Export this run has already staged is found in the working
        // set, so EnsureDeletePendingExportAsync answers "what is attached to this CSO?" without reading this
        // run's own write back from the database.
        var workingSet = new ExportEvaluationWorkingSet();
        var csoId = Guid.NewGuid();
        var stagedPe = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Delete };

        workingSet.RecordStagedDeleteExport(csoId, stagedPe);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(workingSet.TryGetStagedDeleteExport(csoId, out var found), Is.True);
            Assert.That(found, Is.SameAs(stagedPe));
        }
    }

    [Test]
    public void WorkingSet_ACsoNoStagedDeleteExportWasRecordedFor_ReportsNone()
    {
        Assert.That(new ExportEvaluationWorkingSet().TryGetStagedDeleteExport(Guid.NewGuid(), out _), Is.False);
    }

    private static SyncRule ExportRule(OutboundDeprovisionAction action) => new()
    {
        Name = $"Export rule ({action})",
        ConnectedSystemId = 1,
        ConnectedSystemObjectTypeId = 5,
        MetaverseObjectTypeId = 100,
        Direction = SyncRuleDirection.Export,
        Enabled = true,
        OutboundDeprovisionAction = action
    };

    private static MetaverseObject Mvo(
        int remainingCsoCount, MetaverseObjectOrigin origin, MetaverseObjectDeletionRule deletionRule)
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = origin,
            Type = new MetaverseObjectType { Id = 100, Name = "User", DeletionRule = deletionRule }
        };

        for (var i = 0; i < remainingCsoCount; i++)
            mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = i + 1 });

        return mvo;
    }
}
