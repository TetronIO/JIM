// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// What a Connector Space clear's Activity says about the Delta Import starting points it removed (#2724), so the reset
/// is visible to whoever reads the Activity, through the portal, the REST API or PowerShell.
/// </summary>
[TestFixture]
public class ClearConnectedSystemResultTests
{
    [Test]
    public void DescribeDeltaImportReset_NoWatermarkRemoved_ReturnsNull()
    {
        var result = new ClearConnectedSystemResult { ConnectedSystemObjectsRemoved = 12, PendingExportsRemoved = 3 };

        Assert.That(result.DescribeDeltaImportReset(), Is.Null);
    }

    [Test]
    public void DescribeDeltaImportReset_ConnectedSystemWatermarkRemoved_SaysSo()
    {
        var result = new ClearConnectedSystemResult { ImportWatermarkRemoved = true };

        var description = result.DescribeDeltaImportReset();

        Assert.That(description, Is.Not.Null);
        Assert.That(description!, Does.Contain("Connected System's Delta Import watermark"));
        Assert.That(description!, Does.Not.Contain("partition"));
    }

    [TestCase(1)]
    [TestCase(3)]
    public void DescribeDeltaImportReset_PartitionWatermarksRemoved_CountsThem(int removed)
    {
        var result = new ClearConnectedSystemResult { PartitionWatermarksRemoved = removed };

        var description = result.DescribeDeltaImportReset();

        Assert.That(description, Is.Not.Null);
        Assert.That(description!, Does.Contain($"{removed} partition"));
        Assert.That(description!, Does.Not.Contain("Connected System's"));
    }

    [Test]
    public void DescribeDeltaImportReset_BothRemoved_NamesBoth()
    {
        var result = new ClearConnectedSystemResult { ImportWatermarkRemoved = true, PartitionWatermarksRemoved = 2 };

        var description = result.DescribeDeltaImportReset();

        Assert.That(description, Is.Not.Null);
        Assert.That(description!, Does.Contain("Connected System's Delta Import watermark"));
        Assert.That(description!, Does.Contain("2 partition"));
    }
}
