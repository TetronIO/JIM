// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// A partition's Selected flag is the administrator saying which parts of a Connected System JIM manages. It was
/// honoured on one import path and ignored on the other: the unscoped path filtered on it, while a Run Profile that
/// named a partition imported that partition whether or not it was still selected. Deselecting a partition therefore
/// did nothing at all for anyone whose Full Import targets a partition, while doing everything (mass obsoletion) for
/// anyone whose Full Import does not.
///
/// These fix the meaning of the flag in one place, so both paths and every caller answer the question the same way.
/// </summary>
[TestFixture]
public class ConnectedSystemTargetPartitionTests
{
    private static ConnectedSystemPartition Partition(int id, string name, bool selected) =>
        new() { Id = id, Name = name, ExternalId = $"DC={name}", Selected = selected };

    private static ConnectedSystem SystemWith(params ConnectedSystemPartition[] partitions) =>
        new() { Id = 1, Name = "Resurgam AD", Partitions = [.. partitions] };

    private static ConnectedSystemRunProfile RunProfile(ConnectedSystemPartition? partition) =>
        new() { Id = 10, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, Partition = partition };

    [Test]
    public void GetTargetPartitions_RunProfileTargetsSelectedPartition_ReturnsThatPartition()
    {
        var selected = Partition(1, "corp", true);
        var connectedSystem = SystemWith(selected, Partition(2, "dmz", true));

        var result = connectedSystem.GetTargetPartitions(RunProfile(selected)).ToList();

        Assert.That(result, Is.EqualTo(new[] { selected }));
    }

    [Test]
    public void GetTargetPartitions_RunProfileTargetsDeselectedPartition_ReturnsNothing()
    {
        // The defect this whole layer exists for: the Run Profile named the partition, so the Selected flag was
        // never consulted and the import proceeded over scope the administrator had removed from management.
        var deselected = Partition(1, "corp", false);
        var connectedSystem = SystemWith(deselected, Partition(2, "dmz", true));

        var result = connectedSystem.GetTargetPartitions(RunProfile(deselected)).ToList();

        Assert.That(result, Is.Empty, "a deselected partition is not managed, however the Run Profile is pointed");
    }

    [Test]
    public void GetTargetPartitions_RunProfileTargetsNoPartition_ReturnsOnlySelectedPartitions()
    {
        var selected = Partition(1, "corp", true);
        var connectedSystem = SystemWith(selected, Partition(2, "dmz", false));

        var result = connectedSystem.GetTargetPartitions(RunProfile(null)).ToList();

        Assert.That(result, Is.EqualTo(new[] { selected }));
    }

    [Test]
    public void GetTargetPartitions_NoPartitionsEnumerated_ReturnsNothing()
    {
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Resurgam AD", Partitions = null };

        var result = connectedSystem.GetTargetPartitions(RunProfile(null)).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void TargetsADeselectedPartition_RunProfileTargetsDeselectedPartition_IsTrue()
    {
        var deselected = Partition(1, "corp", false);
        var connectedSystem = SystemWith(deselected, Partition(2, "dmz", true));

        Assert.That(connectedSystem.TargetsADeselectedPartition(RunProfile(deselected)), Is.True);
    }

    [Test]
    public void TargetsADeselectedPartition_RunProfileTargetsSelectedPartition_IsFalse()
    {
        var selected = Partition(1, "corp", true);
        var connectedSystem = SystemWith(selected);

        Assert.That(connectedSystem.TargetsADeselectedPartition(RunProfile(selected)), Is.False);
    }

    [Test]
    public void TargetsADeselectedPartition_RunProfileTargetsNoPartition_IsFalse()
    {
        // An unscoped Run Profile follows whatever is selected, so it is never left pointing at nothing.
        var connectedSystem = SystemWith(Partition(1, "corp", false));

        Assert.That(connectedSystem.TargetsADeselectedPartition(RunProfile(null)), Is.False);
    }

    [Test]
    public void TargetsADeselectedPartition_PartitionRemovedFromTheDirectory_IsTrue()
    {
        // The Run Profile still references a partition the hierarchy no longer carries: equally inoperable, and the
        // administrator has even less chance of noticing.
        var removed = Partition(99, "retired", true);
        var connectedSystem = SystemWith(Partition(1, "corp", true));

        Assert.That(connectedSystem.TargetsADeselectedPartition(RunProfile(removed)), Is.True);
    }
}
