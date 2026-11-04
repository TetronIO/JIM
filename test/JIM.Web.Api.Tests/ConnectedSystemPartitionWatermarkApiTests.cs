// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The partitions endpoint, and so <c>Get-JIMConnectedSystemPartition</c>, carries each partition's current Delta Import
/// watermark (#2715, Decision 3): read parity with the Scope tab, which says where each selected partition's next Delta
/// Import starts and when it was last recorded.
/// </summary>
[TestFixture]
public class ConnectedSystemPartitionWatermarkApiTests
{
    private const string CorpWatermark = "{\"Kind\":\"Accesslog\",\"AccesslogTimestamp\":\"20261103100541.000000Z\",\"AccesslogTimestampIsLogged\":true}";
    private static readonly DateTime RecordedAt = new(2026, 11, 3, 10, 17, 2, DateTimeKind.Utc);

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _application = new JimApplication(mockRepository.Object);
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task GetConnectedSystemPartitionsAsync_PartitionsWithAndWithoutAWatermark_ReturnEachPartitionsCurrentWatermarkAsync()
    {
        ArrangeConnectedSystem(ConnectorConstants.LdapConnectorName);
        _mockConnectedSystemRepo.Setup(r => r.GetPartitionWatermarksAsync(7)).ReturnsAsync(
            [new ConnectedSystemPartitionWatermark { PartitionId = 70, ConnectedSystemId = 7, Value = CorpWatermark, RecordedAt = RecordedAt }]);

        var partitions = await GetPartitionsAsync();

        var corp = partitions.Single(p => p.Id == 70).DeltaImportWatermark;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(corp?.Value, Is.EqualTo(CorpWatermark));
            Assert.That(corp?.RecordedAt, Is.EqualTo(RecordedAt));
            Assert.That(corp?.Description?.Kind, Is.EqualTo("access log"));
            Assert.That(corp?.Description?.Points.Single().Time, Is.EqualTo(new DateTime(2026, 11, 3, 10, 5, 41, DateTimeKind.Utc)));

            // No row: the partition's next Delta Import reads every object in it.
            Assert.That(partitions.Single(p => p.Id == 80).DeltaImportWatermark, Is.Null);
        }
    }

    [Test]
    public async Task GetConnectedSystemPartitionsAsync_ConnectorWithoutPartitionWatermarks_LeavesEveryWatermarkEmptyAsync()
    {
        ArrangeConnectedSystem(ConnectorConstants.FileConnectorName);

        var partitions = await GetPartitionsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(partitions.Select(p => p.DeltaImportWatermark), Is.All.Null);
            _mockConnectedSystemRepo.Verify(r => r.GetPartitionWatermarksAsync(It.IsAny<int>()), Times.Never);
        }
    }

    private async Task<List<ConnectedSystemPartitionDto>> GetPartitionsAsync()
    {
        var result = await _controller.GetConnectedSystemPartitionsAsync(7) as OkObjectResult;
        Assert.That(result?.Value, Is.Not.Null);
        return ((IEnumerable<ConnectedSystemPartitionDto>)result!.Value!).ToList();
    }

    private void ArrangeConnectedSystem(string connectorName)
    {
        var connectedSystem = new ConnectedSystem { Id = 7, Name = "Directory", ConnectorDefinition = new ConnectorDefinition { Name = connectorName } };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(7, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemPartitionsAsync(It.IsAny<ConnectedSystem>())).ReturnsAsync(() => new List<ConnectedSystemPartition>
        {
            new() { Id = 70, Name = "dc=corp,dc=local", ExternalId = "dc=corp,dc=local", Selected = true, ConnectedSystem = connectedSystem },
            new() { Id = 80, Name = "dc=partners,dc=local", ExternalId = "dc=partners,dc=local", Selected = true, ConnectedSystem = connectedSystem }
        });
    }
}
