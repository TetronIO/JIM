// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The Connector Space list's rows carry the same derived connection State the Metaverse Object's
/// Connections tab shows (D-S7 on #1519's plan: one derivation, reused rather than re-implemented).
/// The repository projects the Pending Export's status and change type as scalars; the state itself is
/// resolved here, in JIM.Application, because a resolver call cannot be translated into SQL and
/// JIM.PostgresData sits below the layer the resolver lives in.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectHeaderStateTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    private static ConnectedSystemObjectHeader CreateHeader(
        ConnectedSystemObjectStatus status,
        PendingExportStatus? pendingExportStatus = null,
        PendingExportChangeType? pendingExportChangeType = null) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = 1,
        Status = status,
        HasPendingExport = pendingExportStatus != null,
        PendingExportStatus = pendingExportStatus,
        PendingExportChangeType = pendingExportChangeType
    };

    [Test]
    public async Task GetConnectedSystemObjectHeadersAsync_FailedPendingExport_RowStateIsExportFailedAsync()
    {
        var header = CreateHeader(ConnectedSystemObjectStatus.Normal, PendingExportStatus.Failed, PendingExportChangeType.Update);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectHeadersAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<IEnumerable<ConnectedSystemObjectStatus>?>(), It.IsAny<IEnumerable<int>?>(), It.IsAny<IEnumerable<ConnectedSystemObjectJoinType>?>()))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectHeader> { Results = [header], TotalResults = 1, CurrentPage = 1, PageSize = 20 });

        var result = await _application.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(1);

        Assert.That(result.Results[0].State, Is.EqualTo(ConnectedSystemObjectConnectionState.ExportFailed));
    }

    [Test]
    public async Task GetConnectedSystemObjectHeadersAsync_NoPendingExport_RowStateIsInSyncAsync()
    {
        var header = CreateHeader(ConnectedSystemObjectStatus.Normal);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectHeadersAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<IEnumerable<ConnectedSystemObjectStatus>?>(), It.IsAny<IEnumerable<int>?>(), It.IsAny<IEnumerable<ConnectedSystemObjectJoinType>?>()))
            .ReturnsAsync(new PagedResultSet<ConnectedSystemObjectHeader> { Results = [header], TotalResults = 1, CurrentPage = 1, PageSize = 20 });

        var result = await _application.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(1);

        Assert.That(result.Results[0].State, Is.EqualTo(ConnectedSystemObjectConnectionState.InSync));
    }

    [Test]
    public async Task GetConnectedSystemObjectHeadersRangeAsync_PendingCreate_RowStateIsProvisioningExportPendingAsync()
    {
        // The virtualised list reads through the range overload, so it needs the same treatment: a State
        // resolved on one path and left default on the other is exactly the drift D-S7 exists to prevent.
        var header = CreateHeader(ConnectedSystemObjectStatus.PendingProvisioning, PendingExportStatus.Pending, PendingExportChangeType.Create);
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemObjectHeadersRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<IEnumerable<ConnectedSystemObjectStatus>?>(), It.IsAny<IEnumerable<int>?>(), It.IsAny<IEnumerable<ConnectedSystemObjectJoinType>?>(), It.IsAny<bool>()))
            .ReturnsAsync(new RangeResultSet<ConnectedSystemObjectHeader> { Results = [header], TotalResults = 1 });

        var result = await _application.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(1, 0, 20);

        Assert.That(result.Results[0].State, Is.EqualTo(ConnectedSystemObjectConnectionState.ProvisioningExportPending));
    }
}
