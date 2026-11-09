// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Scale guard for Pending Export reconciliation over probed values (#2850). The confirming Full Import of a 500,000
/// user initial load spent 578.5 s in <c>ProcessReconciliation</c>, 284.9 s of it on one page: every Add confirming a
/// large group's membership scanned every candidate row the probe returned for the group, so the cost grew with the
/// square of the membership. This confirms a group's whole membership and requires it to take time that grows with the
/// membership, not its square.
/// </summary>
[TestFixture]
public class SyncEngineReconciliationProbeScaleTests
{
    private const int MemberCount = 30_000;
    private const int AttributeId = 10;

    /// <summary>
    /// Generous for a linear pass over 30,000 rows and changes (tens of milliseconds); the quadratic scan it replaces
    /// takes many seconds at this size.
    /// </summary>
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(2);

    private static readonly ConnectedSystemObjectTypeAttribute Member = new()
    {
        Id = AttributeId, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
    };

    private static string Dn(int index) => $"uid=user{index:D6},ou=People,dc=glitterband,dc=local";

    /// <summary>
    /// The initial export of a 30,000-member group, confirmed: each member an Add naming its object and the DN export
    /// resolution stamped, and the probe's candidate rows the group's 30,000 imported members, resolved, in the order
    /// the database happened to return them (here the reverse of the changes, so a scan finds each half way on average).
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_ThirtyThousandMemberGroupOverProbedValues_ConfirmsInTimeLinearInTheMembership()
    {
        var engine = new SyncEngine();
        var groupId = Guid.NewGuid();
        var memberIds = Enumerable.Range(0, MemberCount).Select(_ => Guid.NewGuid()).ToList();

        var changes = memberIds.Select((memberId, index) => new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = AttributeId, Attribute = Member, ChangeType = PendingExportAttributeChangeType.Add,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            ResolvedReferenceCsoId = memberId, StringValue = Dn(index)
        }).ToList();

        // As the probe's repository returns them: id, attribute id and the two reference fields, no attribute definition.
        var candidates = memberIds.Select((memberId, index) => new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = AttributeId, ReferenceValueId = memberId, UnresolvedReferenceValue = Dn(index)
        }).Reverse().ToList();

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemObjectId = groupId, ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Exported, AttributeValueChanges = [.. changes]
        };
        var probed = new Dictionary<int, ProbedAttributeValues>
        {
            [AttributeId] = new()
            {
                ConnectedSystemObjectId = groupId, AttributeId = AttributeId,
                ProbedChangeIds = changes.Select(c => c.Id).ToHashSet(), CandidateValues = candidates
            }
        };
        var result = new PendingExportReconciliationResult();

        var stopwatch = Stopwatch.StartNew();
        engine.ReconcileCsoAgainstPendingExport(new ConnectedSystemObject { Id = groupId }, pendingExport, result,
            probedValuesByAttributeId: probed);
        stopwatch.Stop();
        TestContext.Out.WriteLine($"Confirmed {MemberCount:N0} members over {MemberCount:N0} candidate rows in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConfirmedChanges, Has.Count.EqualTo(MemberCount), "every member the group was given is confirmed");
            Assert.That(result.RetryChanges, Is.Empty);
            Assert.That(result.PendingExportDeleted, Is.True);
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeBudget),
                $"confirming {MemberCount:N0} members took {stopwatch.Elapsed.TotalSeconds:N1}s; each change must be judged from one lookup " +
                "of the probed rows, not a scan of them");
        }
    }
}
