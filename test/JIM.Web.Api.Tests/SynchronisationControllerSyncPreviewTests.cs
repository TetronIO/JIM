// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="SynchronisationController.GetConnectedSystemObjectSyncPreviewAsync"/> (#1519):
/// the REST surface over <see cref="JIM.Application.Servers.SyncPreviewServer.PreviewSyncForCsoAsync"/>.
/// </summary>
[TestFixture]
public class SynchronisationControllerSyncPreviewTests
{
    private const int ConnectedSystemId = 1;

    private Mock<ISyncRepository> _mockSyncRepository = null!;
    private Mock<IExpressionEvaluator> _mockExpressionEvaluator = null!;
    private Mock<ICredentialProtectionService> _mockCredentialProtection = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockSyncRepository = new Mock<ISyncRepository>();
        _mockExpressionEvaluator = new Mock<IExpressionEvaluator>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();

        // The preview path always begins the same way: an (optional) rollback-only transaction, then loads
        // the object. A null transaction handle is a valid "nothing to dispose" answer.
        _mockSyncRepository.Setup(r => r.BeginRollbackOnlyTransactionAsync())
            .ReturnsAsync((IAsyncDisposable?)null);

        _application = new JimApplication(Mock.Of<JIM.Data.IRepository>(), syncRepository: _mockSyncRepository.Object);
        _controller = new SynchronisationController(
            Mock.Of<ILogger<SynchronisationController>>(),
            _application,
            _mockExpressionEvaluator.Object,
            _mockCredentialProtection.Object);
    }

    [Test]
    public async Task GetConnectedSystemObjectSyncPreviewAsync_ObjectDoesNotExist_ReturnsNotFound()
    {
        _mockSyncRepository
            .Setup(r => r.GetConnectedSystemObjectAsync(ConnectedSystemId, It.IsAny<Guid>()))
            .ReturnsAsync((ConnectedSystemObject?)null);

        var result = await _controller.GetConnectedSystemObjectSyncPreviewAsync(ConnectedSystemId, Guid.NewGuid());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        var notFound = (NotFoundObjectResult)result;
        Assert.That(notFound.Value, Is.InstanceOf<ApiErrorResponse>());
    }

    [Test]
    public async Task GetConnectedSystemObjectSyncPreviewAsync_ObjectHasNoApplicableSyncRule_ReturnsOkWithWarning()
    {
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject { Id = csoId, TypeId = 5, ConnectedSystemId = ConnectedSystemId };

        _mockSyncRepository
            .Setup(r => r.GetConnectedSystemObjectAsync(ConnectedSystemId, csoId))
            .ReturnsAsync(cso);

        // No enabled import Synchronisation Rules of any kind: the inbound chain stops immediately with a
        // NoApplicableSyncRule warning, which is a real, achievable 200 that does not need the rest of the
        // outbound machinery mocked out.
        _mockSyncRepository.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, false, false))
            .ReturnsAsync([]);
        _mockSyncRepository.Setup(r => r.GetAllSyncRulesAsync(false))
            .ReturnsAsync([]);
        _mockSyncRepository.Setup(r => r.GetObjectTypesAsync(ConnectedSystemId))
            .ReturnsAsync([]);

        var result = await _controller.GetConnectedSystemObjectSyncPreviewAsync(ConnectedSystemId, csoId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result;
        var response = ok.Value as SyncPreviewResponse;
        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response!.HasBlockingErrors, Is.False);
            Assert.That(response.Errors, Is.Empty);
            Assert.That(response.Warnings, Has.Count.EqualTo(1));
            Assert.That(response.Warnings[0].Code, Is.EqualTo(SyncPreviewMessageCode.NoApplicableSyncRule));
            Assert.That(response.Inbound, Is.Not.Null);
            Assert.That(response.ProposedExports, Is.Empty);
            Assert.That(response.OutcomeTree, Is.Empty);
        }
    }
}
