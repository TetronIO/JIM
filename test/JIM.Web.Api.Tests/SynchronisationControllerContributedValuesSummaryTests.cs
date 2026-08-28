// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the contributed-values summary endpoints (#1537): the counts a deletion surface shows before the
/// administrator chooses to recall or keep a Synchronisation Rule's (or one mapping's) contributed values.
/// </summary>
[TestFixture]
public class SynchronisationControllerContributedValuesSummaryTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<SynchronisationController>> _mockLogger = null!;
    private Mock<ICredentialProtectionService> _mockCredentialProtection = null!;
    private IExpressionEvaluator _expressionEvaluator = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);

        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        // Authenticate the controller via an API key, matching the neighbouring Synchronisation Rule endpoint tests.
        var apiKeyId = Guid.NewGuid();
        var apiKey = new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId))
            .ReturnsAsync(apiKey);

        var claims = new List<Claim>
        {
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    private static ContributedValuesSummary BuildSummary()
    {
        return new ContributedValuesSummary
        {
            Attributes =
            [
                new ContributedValuesAttributeSummary { AttributeId = 5, AttributeName = "displayName", ValueCount = 3, ObjectCount = 2 },
                new ContributedValuesAttributeSummary { AttributeId = 6, AttributeName = "mobile", ValueCount = 1, ObjectCount = 1 }
            ],
            TotalObjects = 2
        };
    }

    #region Synchronisation Rule summary

    [Test]
    public async Task GetSyncRuleContributedValuesSummaryAsync_WithContributedValues_ReturnsSummaryAsync()
    {
        var syncRule = new SyncRule { Id = 1, Name = "HR Import Rule" };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(1))
            .ReturnsAsync(syncRule);
        _mockMetaverseRepo.Setup(r => r.GetContributedValuesSummaryAsync(1, null))
            .ReturnsAsync(BuildSummary());

        var result = await _controller.GetSyncRuleContributedValuesSummaryAsync(1) as OkObjectResult;
        var dto = result?.Value as ContributedValuesSummaryDto;

        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.Attributes, Has.Count.EqualTo(2));
            Assert.That(dto!.Attributes[0].AttributeId, Is.EqualTo(5));
            Assert.That(dto!.Attributes[0].AttributeName, Is.EqualTo("displayName"));
            Assert.That(dto!.Attributes[0].ValueCount, Is.EqualTo(3));
            Assert.That(dto!.Attributes[0].ObjectCount, Is.EqualTo(2));
            Assert.That(dto!.TotalValues, Is.EqualTo(4));
            Assert.That(dto!.TotalObjects, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task GetSyncRuleContributedValuesSummaryAsync_WithNonExistentSyncRule_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(999))
            .ReturnsAsync((SyncRule?)null);

        var result = await _controller.GetSyncRuleContributedValuesSummaryAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region Mapping summary

    [Test]
    public async Task GetSyncRuleMappingContributedValuesSummaryAsync_WithImportMapping_ReturnsScopedSummaryAsync()
    {
        var syncRule = new SyncRule { Id = 1, Name = "HR Import Rule" };
        var mapping = new SyncRuleMapping
        {
            Id = 10,
            SyncRule = syncRule,
            SyncRuleId = 1,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 5, Name = "displayName" },
            TargetMetaverseAttributeId = 5
        };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(1))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(10))
            .ReturnsAsync(mapping);
        _mockMetaverseRepo.Setup(r => r.GetContributedValuesSummaryAsync(1, 5))
            .ReturnsAsync(new ContributedValuesSummary
            {
                Attributes =
                [
                    new ContributedValuesAttributeSummary { AttributeId = 5, AttributeName = "displayName", ValueCount = 3, ObjectCount = 2 }
                ],
                TotalObjects = 2
            });

        var result = await _controller.GetSyncRuleMappingContributedValuesSummaryAsync(1, 10) as OkObjectResult;
        var dto = result?.Value as ContributedValuesSummaryDto;

        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.Attributes, Has.Count.EqualTo(1));
            Assert.That(dto!.Attributes[0].AttributeName, Is.EqualTo("displayName"));
            Assert.That(dto!.TotalValues, Is.EqualTo(3));
            Assert.That(dto!.TotalObjects, Is.EqualTo(2));
        }
        _mockMetaverseRepo.Verify(r => r.GetContributedValuesSummaryAsync(1, 5), Times.Once,
            "the mapping summary must be scoped to the mapping's target Metaverse Attribute");
    }

    [Test]
    public async Task GetSyncRuleMappingContributedValuesSummaryAsync_WithExportMapping_ReturnsEmptySummaryAsync()
    {
        // An export mapping (or one with no target Metaverse Attribute) contributes nothing to the Metaverse,
        // so its summary is empty and no count query runs.
        var syncRule = new SyncRule { Id = 2, Name = "AD Export Rule", Direction = SyncRuleDirection.Export };
        var mapping = new SyncRuleMapping
        {
            Id = 11,
            SyncRule = syncRule,
            SyncRuleId = 2,
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 20, Name = "cn" },
            TargetConnectedSystemAttributeId = 20
        };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(2))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(11))
            .ReturnsAsync(mapping);

        var result = await _controller.GetSyncRuleMappingContributedValuesSummaryAsync(2, 11) as OkObjectResult;
        var dto = result?.Value as ContributedValuesSummaryDto;

        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.Attributes, Is.Empty);
            Assert.That(dto!.TotalValues, Is.EqualTo(0));
            Assert.That(dto!.TotalObjects, Is.EqualTo(0));
        }
        _mockMetaverseRepo.Verify(r => r.GetContributedValuesSummaryAsync(It.IsAny<int>(), It.IsAny<int?>()), Times.Never);
    }

    [Test]
    public async Task GetSyncRuleMappingContributedValuesSummaryAsync_WithNonExistentSyncRule_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(999))
            .ReturnsAsync((SyncRule?)null);

        var result = await _controller.GetSyncRuleMappingContributedValuesSummaryAsync(999, 10);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingContributedValuesSummaryAsync_WithNonExistentMapping_ReturnsNotFoundAsync()
    {
        var syncRule = new SyncRule { Id = 1, Name = "HR Import Rule" };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(1))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(999))
            .ReturnsAsync((SyncRuleMapping?)null);

        var result = await _controller.GetSyncRuleMappingContributedValuesSummaryAsync(1, 999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingContributedValuesSummaryAsync_WithMappingFromDifferentRule_ReturnsNotFoundAsync()
    {
        var syncRule = new SyncRule { Id = 1, Name = "HR Import Rule" };
        var differentRule = new SyncRule { Id = 2, Name = "Different Rule" };
        var mapping = new SyncRuleMapping
        {
            Id = 10,
            SyncRule = differentRule,
            SyncRuleId = 2
        };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(1))
            .ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(10))
            .ReturnsAsync(mapping);

        var result = await _controller.GetSyncRuleMappingContributedValuesSummaryAsync(1, 10);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion
}
