// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The Connector Space grid cancels windows it no longer wants; the application layer must hand that token to the
/// repository rather than dropping it, or the cancellation stops at the facade while the query runs on.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectHeaderRangeCancellationTests
{
    [Test]
    public async Task GetConnectedSystemObjectHeadersRangeAsync_WithToken_PassesItToTheRepositoryAsync()
    {
        var repository = new Mock<IRepository>();
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);
        CancellationToken received = default;
        connectedSystems.Setup(r => r.GetConnectedSystemObjectHeadersRangeAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<IEnumerable<ConnectedSystemObjectStatus>?>(), It.IsAny<IEnumerable<int>?>(),
                It.IsAny<IEnumerable<ConnectedSystemObjectJoinType>?>(), It.IsAny<bool>(), It.IsAny<bool?>(),
                It.IsAny<CancellationToken>()))
            .Callback((int _, int _, int _, string? _, string? _, bool _, IEnumerable<ConnectedSystemObjectStatus>? _,
                IEnumerable<int>? _, IEnumerable<ConnectedSystemObjectJoinType>? _, bool _, bool? _, CancellationToken token) => received = token)
            .ReturnsAsync(new RangeResultSet<ConnectedSystemObjectHeader> { Results = [], TotalResults = 0 });
        var jim = new JimApplication(repository.Object);
        using var cancellation = new CancellationTokenSource();

        await jim.ConnectedSystems.GetConnectedSystemObjectHeadersRangeAsync(1, 0, 20, cancellationToken: cancellation.Token);

        Assert.That(received, Is.EqualTo(cancellation.Token));
    }
}
