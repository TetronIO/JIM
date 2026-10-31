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
/// Scale guard for merging a large membership change into a large staged Pending Export (#2658). A group provisioned with
/// 50,000 members stages a Pending Export holding an Add per member, and a synchronisation that re-evaluates the group
/// before that export is confirmed stages every member again. Each incoming change that collided with a staged one
/// re-keyed every staged change to find the ones it replaced, then removed each from the list one at a time, so the
/// merge cost the square of the membership. The bound is generous, so a return of that rescan fails loudly rather than
/// flakily.
/// </summary>
[TestFixture]
public class SyncEngineExportMergeScaleTests
{
    private const int MemberCount = 50_000;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly ConnectedSystemObjectTypeAttribute Member = new()
    {
        Id = 2, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
    };

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    [Test]
    public void MergeAttributeChangesIntoPendingExport_FiftyThousandMembersStagedAgainWithTwoMore_MergesWithinBound()
    {
        var memberIds = Enumerable.Range(0, MemberCount).Select(_ => Guid.NewGuid().ToString()).ToList();
        var stagedPendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            ConnectedSystemObjectId = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            AttributeValueChanges = new(memberIds.Select(MemberAdd))
        };

        // The re-evaluation stages every member the export already holds, and two who have joined since.
        var incoming = memberIds.Concat([Guid.NewGuid().ToString(), Guid.NewGuid().ToString()]).Select(MemberAdd).ToList();

        var stopwatch = Stopwatch.StartNew();
        var result = _engine.MergeAttributeChangesIntoPendingExport(stagedPendingExport, incoming);
        stopwatch.Stop();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReplacedCount, Is.EqualTo(MemberCount), "every staged member is replaced by its newly evaluated change");
            Assert.That(result.AddedCount, Is.EqualTo(2));
            Assert.That(stagedPendingExport.AttributeValueChanges, Is.EqualTo(incoming),
                "the export holds exactly the newly evaluated changes, in the order they were staged");
            Assert.That(stopwatch.Elapsed, Is.LessThan(Bound),
                $"merging {incoming.Count:N0} member changes into a Pending Export holding {MemberCount:N0} took {stopwatch.Elapsed.TotalSeconds:N1}s; " +
                "the staged changes must be keyed once and the superseded ones removed in one pass, not rescanned per change");
        }
    }

    private static PendingExportAttributeValueChange MemberAdd(string memberMetaverseObjectId) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = Member,
        AttributeId = Member.Id,
        ChangeType = PendingExportAttributeChangeType.Add,
        UnresolvedReferenceValue = memberMetaverseObjectId
    };
}
