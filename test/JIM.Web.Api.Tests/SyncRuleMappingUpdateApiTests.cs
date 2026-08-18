// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Changing an existing Attribute Flow's settings over the REST API.
/// </summary>
/// <remarks>
/// Until now the API could create and delete an Attribute Flow but never change one, so every per-mapping
/// setting the portal offers (Missing Input Behaviour, Null is a value, Initial Export Only, inbound value
/// processing) could only be corrected by deleting the mapping and building it again, which discards its
/// Attribute Priority position. What a mapping *targets* is deliberately still not editable: retargeting changes
/// what the mapping is, and delete-and-recreate is the honest way to express that.
/// </remarks>
[TestFixture]
public class SyncRuleMappingUpdateApiTests
{
    private const int ImportRuleId = 1;
    private const int ExportRuleId = 2;
    private const int ObjectTypeId = 7;
    private const int ExpressionMappingId = 10;
    private const int AttributeMappingId = 11;
    private const int ExportMappingId = 12;

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private SynchronisationController _controller = null!;
    private SyncRuleMapping _expressionMapping = null!;
    private SyncRuleMapping _attributeMapping = null!;
    private SyncRuleMapping _exportMapping = null!;

    [SetUp]
    public void SetUp()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        var mockMetaverseRepo = new Mock<IMetaverseRepository>();
        var mockActivityRepo = new Mock<IActivityRepository>();
        var mockApiKeyRepo = new Mock<IApiKeyRepository>();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        mockRepository.Setup(r => r.Metaverse).Returns(mockMetaverseRepo.Object);
        mockRepository.Setup(r => r.Activity).Returns(mockActivityRepo.Object);
        mockRepository.Setup(r => r.ApiKeys).Returns(mockApiKeyRepo.Object);
        mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var metaverseAttribute = new MetaverseAttribute { Id = 5, Name = "Email", Type = AttributeDataType.Text };
        var connectedSystemAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 20,
            Name = "mail",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = ObjectTypeId, Name = "user" }
        };

        var importRule = new SyncRule { Id = ImportRuleId, Name = "Import Rule", Direction = SyncRuleDirection.Import, ConnectedSystemObjectTypeId = ObjectTypeId };
        var exportRule = new SyncRule { Id = ExportRuleId, Name = "Export Rule", Direction = SyncRuleDirection.Export, ConnectedSystemObjectTypeId = ObjectTypeId };

        _expressionMapping = new SyncRuleMapping
        {
            Id = ExpressionMappingId,
            SyncRule = importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = metaverseAttribute,
            TargetMetaverseAttributeId = metaverseAttribute.Id,
            Sources = { new SyncRuleMappingSource { Id = 100, Order = 0, Expression = "cs[\"mail\"]" } }
        };
        _attributeMapping = new SyncRuleMapping
        {
            Id = AttributeMappingId,
            SyncRule = importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = metaverseAttribute,
            TargetMetaverseAttributeId = metaverseAttribute.Id,
            Sources = { new SyncRuleMappingSource { Id = 101, Order = 0, ConnectedSystemAttribute = connectedSystemAttribute, ConnectedSystemAttributeId = connectedSystemAttribute.Id } }
        };
        _exportMapping = new SyncRuleMapping
        {
            Id = ExportMappingId,
            SyncRule = exportRule,
            SyncRuleId = ExportRuleId,
            TargetConnectedSystemAttribute = connectedSystemAttribute,
            TargetConnectedSystemAttributeId = connectedSystemAttribute.Id,
            Sources = { new SyncRuleMappingSource { Id = 102, Order = 0, Expression = "mv[\"Email\"]" } }
        };

        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(ImportRuleId)).ReturnsAsync(importRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(ExportRuleId)).ReturnsAsync(exportRule);
        foreach (var mapping in new[] { _expressionMapping, _attributeMapping, _exportMapping })
        {
            _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingForUpdateAsync(mapping.Id)).ReturnsAsync(mapping);
            _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(mapping.Id)).ReturnsAsync(mapping);
        }
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);

        var application = new JimApplication(mockRepository.Object);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        });
        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")) }
        };
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_MissingInputBehaviour_IsAppliedToTheExpressionSourceAsync()
    {
        // The gap this endpoint closes: the behaviour could be set at creation and never changed afterwards.
        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, ExpressionMappingId,
            new UpdateSyncRuleMappingRequest { MissingInputBehaviour = MissingInputBehaviour.FailObject });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (SyncRuleMappingDto)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_expressionMapping.Sources[0].MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.FailObject));
            Assert.That(dto.Sources[0].MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.FailObject));
        }
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleMappingAsync(_expressionMapping), Times.Once);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_Expression_IsRewrittenAndValidatedAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, ExpressionMappingId,
            new UpdateSyncRuleMappingRequest { Expression = "Lower(cs[\"mail\"])" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(_expressionMapping.Sources[0].Expression, Is.EqualTo("Lower(cs[\"mail\"])"));
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_InvalidExpression_ReturnsBadRequestAndChangesNothingAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, ExpressionMappingId,
            new UpdateSyncRuleMappingRequest { Expression = "this is (not" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_expressionMapping.Sources[0].Expression, Is.EqualTo("cs[\"mail\"]"),
            "A rejected Expression must not have been written before the rejection.");
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ExpressionSettingsOnAnAttributeMapping_ReturnsBadRequestAsync()
    {
        // An attribute source has no Expression and so no inputs to be missing; silently ignoring the field
        // would leave the caller believing it had taken effect.
        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, AttributeMappingId,
            new UpdateSyncRuleMappingRequest { MissingInputBehaviour = MissingInputBehaviour.FailMapping });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ImportOnlySettingOnAnExportMapping_ReturnsBadRequestAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(ExportRuleId, ExportMappingId,
            new UpdateSyncRuleMappingRequest { NullIsValue = true });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_exportMapping.NullIsValue, Is.False);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ExportOnlySettingOnAnImportMapping_ReturnsBadRequestAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, ExpressionMappingId,
            new UpdateSyncRuleMappingRequest { InitialExportOnly = true });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_expressionMapping.InitialExportOnly, Is.False);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ImportSettings_AreAppliedAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, ExpressionMappingId,
            new UpdateSyncRuleMappingRequest
            {
                NullIsValue = true,
                InboundValueProcessing = InboundValueProcessing.TrimWhitespace,
                CaseNormalisation = InboundCaseNormalisation.Lower
            });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_expressionMapping.NullIsValue, Is.True);
            Assert.That(_expressionMapping.InboundValueProcessing, Is.EqualTo(InboundValueProcessing.TrimWhitespace));
            Assert.That(_expressionMapping.CaseNormalisation, Is.EqualTo(InboundCaseNormalisation.Lower));
        }
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ExportSetting_IsAppliedAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(ExportRuleId, ExportMappingId,
            new UpdateSyncRuleMappingRequest { InitialExportOnly = true });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(_exportMapping.InitialExportOnly, Is.True);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_NothingSupplied_ReturnsBadRequestAsync()
    {
        // A request naming no setting is far more likely to be a misspelled field than a deliberate no-op, and
        // answering 200 to one would report success for a change that never happened.
        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, ExpressionMappingId,
            new UpdateSyncRuleMappingRequest());

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_MappingBelongingToAnotherRule_ReturnsNotFoundAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, ExportMappingId,
            new UpdateSyncRuleMappingRequest { MissingInputBehaviour = MissingInputBehaviour.FailObject });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        Assert.That(_exportMapping.Sources[0].MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.EvaluateAnyway));
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_UnknownSyncRule_ReturnsNotFoundAsync()
    {
        var result = await _controller.UpdateSyncRuleMappingAsync(999, ExpressionMappingId,
            new UpdateSyncRuleMappingRequest { MissingInputBehaviour = MissingInputBehaviour.FailObject });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }
}
