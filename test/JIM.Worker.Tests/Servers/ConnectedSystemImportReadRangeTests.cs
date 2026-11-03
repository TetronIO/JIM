// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Worker.Tests.Workflows;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers <see cref="JIM.Application.Servers.ConnectedSystemServer.GetImportReadRangeAsync"/>: an import Activity's
/// recorded watermarks are handed back to the Connector that wrote them, so JIM never interprets them itself.
/// </summary>
[TestFixture]
public class ConnectedSystemImportReadRangeTests
{
    private const string ScimProviderFrom = "{\"changeSequence\":8}";
    private const string ScimProviderTo = "{\"changeSequence\":15}";

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
    public async Task GetImportReadRangeAsync_ImportWithWatermarks_DescribesBothThroughItsConnectorAsync()
    {
        ArrangeConnectedSystem(ConnectorConstants.ScimServiceProviderConnectorName);

        var range = await _jim.ConnectedSystems.GetImportReadRangeAsync(Import(ScimProviderFrom, ScimProviderTo));

        Assert.That(range, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(range!.From?.Points.Single().Number, Is.EqualTo(8));
            Assert.That(range!.To?.Points.Single().Number, Is.EqualTo(15));
        }
    }

    [Test]
    public async Task GetImportReadRangeAsync_ImportThatRecordedNoNewWatermark_DescribesOnlyWhereItStartedAsync()
    {
        ArrangeConnectedSystem(ConnectorConstants.ScimServiceProviderConnectorName);

        var range = await _jim.ConnectedSystems.GetImportReadRangeAsync(Import(ScimProviderFrom, null));

        Assert.That(range, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(range!.From, Is.Not.Null);
            Assert.That(range!.To, Is.Null);
        }
    }

    [Test]
    public async Task GetImportReadRangeAsync_ActivityWithNoWatermarks_ReturnsNullWithoutLoadingTheConnectedSystemAsync()
    {
        var range = await _jim.ConnectedSystems.GetImportReadRangeAsync(Import(null, null));

        Assert.That(range, Is.Null);
        _mockConnectedSystemRepo.Verify(r => r.GetConnectedSystemCoreAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task GetImportReadRangeAsync_ConnectedSystemSinceDeleted_ReturnsNullAsync()
    {
        // The Activity outlives its Connected System; with no Connector to ask, only the raw values remain.
        var activity = Import(ScimProviderFrom, ScimProviderTo);
        activity.ConnectedSystemId = null;

        Assert.That(await _jim.ConnectedSystems.GetImportReadRangeAsync(activity), Is.Null);
    }

    [Test]
    public async Task GetImportReadRangeAsync_ConnectorThatDoesNotDescribeWatermarks_ReturnsNullAsync()
    {
        ArrangeConnectedSystem(ConnectorConstants.FileConnectorName);

        Assert.That(await _jim.ConnectedSystems.GetImportReadRangeAsync(Import(ScimProviderFrom, ScimProviderTo)), Is.Null);
    }

    [Test]
    public async Task GetImportReadRangeAsync_PartitionWatermarks_DescribesOnePointPerPartitionScopedByItsNameAsync()
    {
        // #2715: a Connector that keeps a watermark per Partition describes each value, and each Partition becomes a scope.
        // Partners had no watermark before the run, so it has no starting point, and reads "from the start".
        ArrangePartitionWatermarkConnector();
        var activity = Import(null, null);
        activity.ImportPartitionWatermarksFrom = [Watermark(7, "corp.local", "corp-1"), Watermark(8, "partners.local", null)];
        activity.ImportPartitionWatermarksTo = [Watermark(7, "corp.local", "corp-2"), Watermark(8, "partners.local", "partners-1")];

        var range = await _jim.ConnectedSystems.GetImportReadRangeAsync(activity);

        Assert.That(range, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Points(range!.From), Is.EqualTo(new[] { ("corp.local", "corp-1") }));
            Assert.That(Points(range!.To), Is.EqualTo(new[] { ("corp.local", "corp-2"), ("partners.local", "partners-1") }));
            Assert.That(range!.To!.Kind, Is.EqualTo("test position"));
        }
    }

    [Test]
    public async Task GetImportReadRangeAsync_PartitionDeletedSinceTheRun_ScopesByTheRecordedNameAsync()
    {
        // The Activity outlives a rename or removal of the Partition: it reads as the run saw it.
        ArrangePartitionWatermarkConnector(new ConnectedSystemPartition { Id = 7, ExternalId = "dc=corp,dc=local", Name = "corp.example", Selected = true });
        var activity = Import(null, null);
        activity.ImportPartitionWatermarksTo = [Watermark(7, "corp.local", "corp-2"), Watermark(8, "partners.local", "partners-1")];

        var range = await _jim.ConnectedSystems.GetImportReadRangeAsync(activity);

        Assert.That(Points(range?.To), Is.EqualTo(new[] { ("corp.local", "corp-2"), ("partners.local", "partners-1") }));
    }

    /// <summary>
    /// A Connected System whose Connector keeps a watermark per Partition (#2715). No built-in Connector does yet, so the
    /// test Connector is handed out by the factory; it describes a value as one point holding the value itself.
    /// </summary>
    private void ArrangePartitionWatermarkConnector(params ConnectedSystemPartition[] partitions)
    {
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(7, It.IsAny<bool>()))
            .ReturnsAsync(new ConnectedSystem
            {
                Id = 7,
                Name = "Test Connected System",
                ConnectorDefinition = new ConnectorDefinition { Name = "Partitioned Test Connector" },
                Partitions = [.. partitions]
            });

        var factory = new Mock<IConnectorFactory>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<ICredentialProtection?>(), It.IsAny<ICertificateProvider?>()))
            .Returns(() => new PartitionWatermarkTestConnector([]));
        _jim.ConnectedSystems.ConnectorFactory = factory.Object;
    }

    private static ImportPartitionWatermark Watermark(int partitionId, string partitionName, string? value) =>
        new() { PartitionId = partitionId, PartitionName = partitionName, Value = value };

    private static List<(string? Scope, string? Text)>? Points(ImportWatermarkDescription? description) =>
        description?.Points.Select(p => (p.Scope, p.Text)).ToList();

    private void ArrangeConnectedSystem(string connectorName) =>
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(7, It.IsAny<bool>()))
            .ReturnsAsync(new ConnectedSystem { Id = 7, Name = "Test Connected System", ConnectorDefinition = new ConnectorDefinition { Name = connectorName } });

    private static Activity Import(string? from, string? to) => new()
    {
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        ConnectedSystemId = 7,
        ConnectedSystemRunType = ConnectedSystemRunType.DeltaImport,
        ImportWatermarkFrom = from,
        ImportWatermarkTo = to
    };
}
