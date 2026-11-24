// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Logic;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

[TestFixture]
public class ExportEvaluationWorkingSetTests
{
    [Test]
    public void MergeFrom_OtherWorkingSet_AddsEveryDecisionStagedExportRuleCancellationAndDisconnection()
    {
        var csoId = Guid.NewGuid();
        var decision = new MvoDeletionExportDecision { Reason = MvoDeletionExportReason.NoMatchingExportRule };
        var stagedExport = new PendingExport { Id = Guid.NewGuid() };
        var rule = new SyncRule { Id = 7, Name = "Directory Export" };
        var cancellation = new CancelledProvisioning(Guid.NewGuid(), 1, Guid.NewGuid(), 2);
        var disconnection = new OutboundDisconnection(Guid.NewGuid(), 1, Guid.NewGuid(), 7, "Directory Export");
        var other = new ExportEvaluationWorkingSet();
        other.RecordDeleteDecision(csoId, decision);
        other.RecordStagedDeleteExport(csoId, stagedExport);
        other.RecordDeprovisioningRule(csoId, rule);
        other.RecordCancelledProvisioning(cancellation);
        other.RecordDisconnection(disconnection);
        var target = new ExportEvaluationWorkingSet();

        target.MergeFrom(other);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.TryGetDeleteDecision(csoId, out var mergedDecision), Is.True);
            Assert.That(mergedDecision, Is.EqualTo(decision));
            Assert.That(target.TryGetStagedDeleteExport(csoId, out var mergedExport), Is.True);
            Assert.That(mergedExport, Is.SameAs(stagedExport));
            Assert.That(target.TryGetDeprovisioningRule(csoId, out var mergedRule), Is.True);
            Assert.That(mergedRule, Is.SameAs(rule));
            Assert.That(target.CancelledProvisionings, Is.EqualTo(new[] { cancellation }));
            Assert.That(target.Disconnections, Is.EqualTo(new[] { disconnection }));
        }
    }

    [Test]
    public void MergeFrom_SameConnectedSystemObjectInBoth_TheMergedDecisionWins()
    {
        var csoId = Guid.NewGuid();
        var earlier = new PendingExport { Id = Guid.NewGuid() };
        var later = new PendingExport { Id = Guid.NewGuid() };
        var target = new ExportEvaluationWorkingSet();
        target.RecordStagedDeleteExport(csoId, earlier);
        var other = new ExportEvaluationWorkingSet();
        other.RecordStagedDeleteExport(csoId, later);

        target.MergeFrom(other);

        Assert.That(target.TryGetStagedDeleteExport(csoId, out var merged) ? merged : null, Is.SameAs(later));
    }

    [Test]
    public void MergeFrom_ExistingEntries_KeepsThemBesideTheMergedOnes()
    {
        var keptCsoId = Guid.NewGuid();
        var keptDisconnection = new OutboundDisconnection(Guid.NewGuid(), 1, Guid.NewGuid(), 7, "Directory Export");
        var target = new ExportEvaluationWorkingSet();
        target.RecordStagedDeleteExport(keptCsoId, new PendingExport { Id = Guid.NewGuid() });
        target.RecordDisconnection(keptDisconnection);
        var other = new ExportEvaluationWorkingSet();
        var mergedDisconnection = new OutboundDisconnection(Guid.NewGuid(), 1, Guid.NewGuid(), 7, "Directory Export");
        other.RecordDisconnection(mergedDisconnection);

        target.MergeFrom(other);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.TryGetStagedDeleteExport(keptCsoId, out _), Is.True);
            Assert.That(target.Disconnections, Is.EqualTo(new[] { keptDisconnection, mergedDisconnection }));
        }
    }
}
