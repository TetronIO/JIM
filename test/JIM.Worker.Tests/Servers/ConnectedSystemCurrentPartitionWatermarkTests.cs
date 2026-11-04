// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Worker.Tests.Workflows;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers <see cref="JIM.Application.Servers.ConnectedSystemServer.GetDescribedPartitionWatermarksAsync"/> (#2715,
/// Decision 3): where each Partition's next Delta Import starts, read-only, described by the Connector that wrote the
/// value, for the portal's Scope tab, the partitions endpoint and <c>Get-JIMConnectedSystemPartition</c>.
/// </summary>
[TestFixture]
public class ConnectedSystemCurrentPartitionWatermarkTests
{
    private static readonly DateTime RecordedAt = new(2026, 11, 3, 10, 5, 41, DateTimeKind.Utc);

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _jim = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [Test]
    public async Task GetDescribedPartitionWatermarksAsync_ConnectorKeepsAWatermarkPerPartition_DescribesEachRecordedWatermarkAsync()
    {
        ArrangePartitionWatermarkConnector();
        _mockConnectedSystemRepo.Setup(r => r.GetPartitionWatermarksAsync(7)).ReturnsAsync(
        [
            Row(70, "corp-2", RecordedAt),
            Row(80, "partners-1", RecordedAt.AddHours(-1))
        ]);

        var watermarks = await _jim.ConnectedSystems.GetDescribedPartitionWatermarksAsync(7);

        Assert.That(watermarks, Is.Not.Null);
        var byPartition = watermarks!.ToDictionary(w => w.PartitionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(byPartition.Keys, Is.EquivalentTo(new[] { 70, 80 }));
            Assert.That(byPartition[70].Value, Is.EqualTo("corp-2"));
            Assert.That(byPartition[70].RecordedAt, Is.EqualTo(RecordedAt));
            Assert.That(byPartition[70].RecordedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(byPartition[70].Description?.Kind, Is.EqualTo("test position"));
            Assert.That(byPartition[70].Description?.Points.Single().Text, Is.EqualTo("corp-2"));
            Assert.That(byPartition[80].Description?.Points.Single().Text, Is.EqualTo("partners-1"));
            Assert.That(byPartition[80].RecordedAt, Is.EqualTo(RecordedAt.AddHours(-1)));
        }
    }

    [Test]
    public async Task GetDescribedPartitionWatermarksAsync_NoWatermarkRecordedForAnyPartition_ReturnsAnEmptyListAsync()
    {
        // Empty, not null: the Connector keeps watermarks per Partition, so every selected Partition's next Delta Import
        // reads in full and the surfaces say so, rather than saying nothing as for a Connector that keeps none.
        ArrangePartitionWatermarkConnector();
        _mockConnectedSystemRepo.Setup(r => r.GetPartitionWatermarksAsync(7)).ReturnsAsync([]);

        var watermarks = await _jim.ConnectedSystems.GetDescribedPartitionWatermarksAsync(7);

        Assert.That(watermarks, Is.Not.Null.And.Empty);
    }

    [Test]
    public async Task GetDescribedPartitionWatermarksAsync_ValueItsConnectorCannotRead_KeepsTheRawValueWithNoDescriptionAsync()
    {
        // The test Connector reads an empty value as no watermark; the row is still JIM's, so its value and time stand.
        ArrangePartitionWatermarkConnector();
        _mockConnectedSystemRepo.Setup(r => r.GetPartitionWatermarksAsync(7)).ReturnsAsync([Row(70, string.Empty, RecordedAt)]);

        var watermark = (await _jim.ConnectedSystems.GetDescribedPartitionWatermarksAsync(7))?.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(watermark?.Value, Is.Empty);
            Assert.That(watermark?.RecordedAt, Is.EqualTo(RecordedAt));
            Assert.That(watermark?.Description, Is.Null);
        }
    }

    [Test]
    public async Task GetDescribedPartitionWatermarksAsync_RecordedAtReadBackUnspecified_IsReturnedAsUtcAsync()
    {
        // The column is UTC; a read that loses the kind must not have the portal shift it again as if it were local.
        ArrangePartitionWatermarkConnector();
        _mockConnectedSystemRepo.Setup(r => r.GetPartitionWatermarksAsync(7))
            .ReturnsAsync([Row(70, "corp-2", DateTime.SpecifyKind(RecordedAt, DateTimeKind.Unspecified))]);

        var watermark = (await _jim.ConnectedSystems.GetDescribedPartitionWatermarksAsync(7))?.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(watermark?.RecordedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(watermark?.RecordedAt, Is.EqualTo(RecordedAt));
        }
    }

    [Test]
    public async Task GetDescribedPartitionWatermarksAsync_ConnectorWithoutPartitionWatermarks_ReturnsNullWithoutReadingAnyAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(7, It.IsAny<bool>()))
            .ReturnsAsync(new ConnectedSystem { Id = 7, Name = "SCIM clients", ConnectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.ScimServiceProviderConnectorName } });

        var watermarks = await _jim.ConnectedSystems.GetDescribedPartitionWatermarksAsync(7);

        Assert.That(watermarks, Is.Null);
        _mockConnectedSystemRepo.Verify(r => r.GetPartitionWatermarksAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task GetDescribedPartitionWatermarksAsync_ConnectedSystemNotFound_ReturnsNullAsync()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(99, It.IsAny<bool>()))
            .ReturnsAsync((ConnectedSystem?)null);

        Assert.That(await _jim.ConnectedSystems.GetDescribedPartitionWatermarksAsync(99), Is.Null);
        _mockConnectedSystemRepo.Verify(r => r.GetPartitionWatermarksAsync(It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// A Connected System whose Connector keeps a watermark per Partition. The test Connector describes a value as one
    /// point holding the value itself, and an empty one as no watermark.
    /// </summary>
    private void ArrangePartitionWatermarkConnector()
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(7, It.IsAny<bool>()))
            .ReturnsAsync(new ConnectedSystem { Id = 7, Name = "Directory", ConnectorDefinition = new ConnectorDefinition { Name = "Partitioned Test Connector" } });

        var factory = new Mock<IConnectorFactory>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(() => new PartitionWatermarkTestConnector([]));
        _jim.ConnectedSystems.ConnectorFactory = factory.Object;
    }

    private static ConnectedSystemPartitionWatermark Row(int partitionId, string value, DateTime recordedAt) =>
        new() { PartitionId = partitionId, ConnectedSystemId = 7, Value = value, RecordedAt = recordedAt };
}
