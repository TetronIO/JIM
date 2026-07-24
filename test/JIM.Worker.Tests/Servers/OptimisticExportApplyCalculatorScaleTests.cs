// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Scale guard for issue #1079's own regression: full-scale validation (Scale500k25kGroups,
/// 2026-07-21) measured 255 slow <c>OptimisticApply</c> instances totalling 77.5 minutes, all in
/// the group-batch wave. Root cause: <c>OptimisticExportApplyCalculator.ApplyAdd</c> called
/// <c>SyncEngine.ValueExistsOnCso</c> - a linear <c>List.Any()</c> scan - once per Add change,
/// while every accepted Add appended to that same growing list, making a Pending Export with M Add
/// changes O(M^2). The live database has groups up to 495,008 members; O(M^2) at that scale is
/// ~2.4x10^11 comparisons for one group.
/// <para>
/// Mirrors the pattern in <c>Synchronisation/ImportUpdateDiffScaleTests.cs</c> (issue #988 finding
/// 4's equivalent fix for the import diff): a Stopwatch-bounded assertion with a generous margin to
/// stay CI-safe, deliberately far below what even a conservative O(M^2) estimate would take, so the
/// test fails loudly (not flakily) if the quadratic scan ever comes back.
/// </para>
/// </summary>
[TestFixture]
public class OptimisticExportApplyCalculatorScaleTests
{
    /// <summary>
    /// A single Create Pending Export with 100,000 distinct Reference Add changes against an empty
    /// CSO - the worst case for the quadratic scan: every Add's existence probe must scan the whole
    /// (ever-growing) working list because none of the 100,000 values match anything already
    /// present. Reference was chosen because it was the type in the group-membership wave that
    /// measured 77.5 minutes across 255 instances; Text/Number/etc. share the same underlying
    /// per-change ValueExistsOnCso call and are covered by
    /// <see cref="OptimisticExportApplyCalculatorTests"/>'s semantic tests, not scale (they are not
    /// the reported bottleneck).
    /// </summary>
    [Test]
    public void CalculateDelta_100000DistinctReferenceAddsAgainstEmptyCso_CompletesWithinBound()
    {
        const int changeCount = 100_000;
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Type = AttributeDataType.Reference };

        var changes = new List<PendingExportAttributeValueChange>(changeCount);
        for (var i = 0; i < changeCount; i++)
        {
            changes.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                AttributeId = 1,
                Attribute = attribute,
                ChangeType = PendingExportAttributeChangeType.Add,
                UnresolvedReferenceValue = $"CN=Member{i},OU=Users,DC=test,DC=local"
            });
        }

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            AttributeValueChanges = changes
        };

        var stopwatch = Stopwatch.StartNew();
        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);
        stopwatch.Stop();

        Assert.That(delta.Additions, Has.Count.EqualTo(changeCount), "every distinct value should be queued as an addition");
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(0));
        Assert.That(delta.RemovalValueIds, Is.Empty);

        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)),
            $"Applying {changeCount:N0} distinct Reference Add changes against an empty CSO took " +
            $"{stopwatch.Elapsed.TotalSeconds:N1}s; expected well under 10s with the #1079 per-attribute " +
            "index fix applied. The live database has groups up to ~495,008 members; O(M^2) at 100,000 " +
            "alone is 10 billion comparisons - orders of magnitude past this bound if the linear scan regresses.");
    }
}
