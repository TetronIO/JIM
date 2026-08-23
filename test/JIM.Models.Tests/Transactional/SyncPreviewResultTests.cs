// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The composed preview result (#288 plan Phase 3, PRD requirements 1, 2 and 16): the speculative outcome
/// tree, the outbound summary composed from the existing <see cref="ExportEvaluationPreviewResult"/> rather
/// than duplicated counters, and blocking Errors programmatically distinct from advisory Warnings.
/// </summary>
[TestFixture]
public class SyncPreviewResultTests
{
    [Test]
    public void OutboundSummary_ComposesTheExistingPreviewResultCounters()
    {
        // PRD requirement 2: reuse ExportEvaluationPreviewResult's create/update/delete counters rather than
        // re-inventing them; composing the type is what keeps one definition of the counts.
        var result = new SyncPreviewResult();
        result.Outbound.ProposedExports.Add(new PendingExport { ChangeType = PendingExportChangeType.Create });
        result.Outbound.ProposedExports.Add(new PendingExport { ChangeType = PendingExportChangeType.Update });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outbound, Is.InstanceOf<ExportEvaluationPreviewResult>());
            Assert.That(result.Outbound.ObjectsToCreate, Is.EqualTo(1));
            Assert.That(result.Outbound.ObjectsToUpdate, Is.EqualTo(1));
        }
    }

    [Test]
    public void HasBlockingErrors_ReflectsErrorsAlone_NeverWarnings()
    {
        // PRD requirement 16: a consumer distinguishes a blocker from an advisory programmatically, not by
        // string parsing. Warnings must not read as blocking.
        var result = new SyncPreviewResult();
        result.Warnings.Add(new SyncPreviewMessage
        {
            Code = SyncPreviewMessageCode.UnresolvedReference,
            Detail = "manager reference is not yet provisioned to the target"
        });

        Assert.That(result.HasBlockingErrors, Is.False);

        result.Errors.Add(new SyncPreviewMessage
        {
            Code = SyncPreviewMessageCode.ExpressionEvaluationError,
            Detail = "expression failed to evaluate"
        });

        Assert.That(result.HasBlockingErrors, Is.True);
    }

    [Test]
    public void AffectedSyncRules_CarryIdAndNameForEachParticipatingRule()
    {
        var result = new SyncPreviewResult();
        result.AffectedSyncRules.Add(new SyncPreviewSyncRuleReference { Id = 5, Name = "Users to Corporate Directory" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AffectedSyncRules, Has.Count.EqualTo(1));
            Assert.That(result.AffectedSyncRules[0].Id, Is.EqualTo(5));
            Assert.That(result.AffectedSyncRules[0].Name, Is.EqualTo("Users to Corporate Directory"));
        }
    }
}
