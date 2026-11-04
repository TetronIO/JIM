// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using Bunit;
using JIM.Models.Staging;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers when the Scope tab says where a partition's next Delta Import starts (#2715, Decision 3): under each selected
/// partition of a Connector that keeps a watermark per partition, and nowhere else.
/// </summary>
[TestFixture]
public class PartitionDeltaImportWatermarkTests : JimComponentTestContext
{
    private static ConnectedSystemPartition Partition(bool selected) =>
        new() { Id = 70, Name = "dc=corp,dc=local", ExternalId = "dc=corp,dc=local", Selected = selected };

    private static Dictionary<int, DescribedPartitionWatermark> Holding(ImportWatermarkDescription? description) => new()
    {
        [70] = new DescribedPartitionWatermark
        {
            PartitionId = 70,
            Value = "{\"Kind\":\"Usn\",\"Usn\":48213}",
            RecordedAt = DateTime.UtcNow.AddMinutes(-12),
            Description = description
        }
    };

    private static readonly ImportWatermarkDescription Usn = new() { Kind = "Update Sequence Number", Points = [new ImportWatermarkPoint { Number = 48213 }] };

    private IRenderedComponent<PartitionDeltaImportWatermark> RenderLine(ConnectedSystemPartition partition, IReadOnlyDictionary<int, DescribedPartitionWatermark>? watermarks) =>
        Render<PartitionDeltaImportWatermark>(p => p
            .Add(c => c.Partition, partition)
            .Add(c => c.Watermarks, watermarks));

    private static string? State(IRenderedComponent<PartitionDeltaImportWatermark> cut) =>
        cut.Find(".jim-scope-partition-watermark").GetAttribute("data-watermark-state");

    [Test]
    public void PartitionDeltaImportWatermark_SelectedPartitionWithAWatermark_SaysWhereTheNextDeltaImportStarts()
    {
        var cut = RenderLine(Partition(selected: true), Holding(Usn));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(State(cut), Is.EqualTo("recorded"));
            Assert.That(cut.Find(".jim-scope-partition-watermark").TextContent, Does.Contain("48,213 (Update Sequence Number)"));
        }
    }

    [Test]
    public void PartitionDeltaImportWatermark_SelectedPartitionWithNoWatermark_SaysItsNextDeltaImportReadsInFull()
    {
        var cut = RenderLine(Partition(selected: true), new Dictionary<int, DescribedPartitionWatermark>());

        Assert.That(State(cut), Is.EqualTo("none"));
    }

    [Test]
    public void PartitionDeltaImportWatermark_WatermarkItsConnectorCannotRead_SaysItsNextDeltaImportReadsInFull()
    {
        // A value its Connector finds no watermark in is no baseline: the next Delta Import reads in full, as for none.
        var cut = RenderLine(Partition(selected: true), Holding(description: null));

        Assert.That(State(cut), Is.EqualTo("unreadable"));
    }

    [Test]
    public void PartitionDeltaImportWatermark_DeselectedPartition_ShowsNothing()
    {
        // Deselection removes the watermark when it is saved; before then the row still exists, but JIM no longer
        // imports the partition, so where it would start says nothing useful.
        var cut = RenderLine(Partition(selected: false), Holding(Usn));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void PartitionDeltaImportWatermark_ConnectorWithoutPartitionWatermarks_ShowsNothing()
    {
        var cut = RenderLine(Partition(selected: true), null);

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }
}
