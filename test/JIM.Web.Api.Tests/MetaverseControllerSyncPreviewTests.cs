// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="MetaverseController.GetMetaverseObjectSyncPreviewAsync"/> (#1519): the REST surface
/// over <see cref="JIM.Application.Servers.SyncPreviewServer.PreviewSyncForMvoAsync"/>.
/// </summary>
[TestFixture]
public class MetaverseControllerSyncPreviewTests
{
    private Mock<ISyncRepository> _mockSyncRepository = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockSyncRepository = new Mock<ISyncRepository>();
        _mockSyncRepository.Setup(r => r.BeginRollbackOnlyTransactionAsync())
            .ReturnsAsync((IAsyncDisposable?)null);

        _application = new JimApplication(Mock.Of<JIM.Data.IRepository>(), syncRepository: _mockSyncRepository.Object);
        _controller = new MetaverseController(Mock.Of<ILogger<MetaverseController>>(), _application);
    }

    [Test]
    public async Task GetMetaverseObjectSyncPreviewAsync_ObjectDoesNotExist_ReturnsNotFound()
    {
        _mockSyncRepository
            .Setup(r => r.GetMetaverseObjectsByIdsNoTrackingAsync(It.IsAny<System.Collections.Generic.IEnumerable<Guid>>()))
            .ReturnsAsync([]);

        var result = await _controller.GetMetaverseObjectSyncPreviewAsync(Guid.NewGuid());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        var notFound = (NotFoundObjectResult)result;
        Assert.That(notFound.Value, Is.InstanceOf<ApiErrorResponse>());
    }

    [Test]
    public async Task GetMetaverseObjectSyncPreviewAsync_ObjectExistsWithNoExportRules_ReturnsOkWithEmptyOutbound()
    {
        var mvoId = Guid.NewGuid();
        var mvo = new MetaverseObject { Id = mvoId };

        _mockSyncRepository
            .Setup(r => r.GetMetaverseObjectsByIdsNoTrackingAsync(It.IsAny<System.Collections.Generic.IEnumerable<Guid>>()))
            .ReturnsAsync([mvo]);

        // No Synchronisation Rules at all: the export evaluation cache has no export rules and no target
        // systems, so the outbound chain evaluates cleanly to nothing proposed.
        _mockSyncRepository.Setup(r => r.GetAllSyncRulesAsync(false)).ReturnsAsync([]);

        var result = await _controller.GetMetaverseObjectSyncPreviewAsync(mvoId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result;
        var response = ok.Value as SyncPreviewResponse;
        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response!.HasBlockingErrors, Is.False);
            Assert.That(response.Errors, Is.Empty);
            Assert.That(response.ProposedExports, Is.Empty);
            Assert.That(response.OutcomeTree, Is.Empty);
            // An MVO preview has no inbound chain of its own.
            Assert.That(response.Inbound, Is.Null);
        }
    }
}
