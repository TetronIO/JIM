// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
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
/// Missing Input Behaviour across the REST surface (#1361): the API is what PowerShell and every automation
/// caller goes through, so a setting the portal can reach and the API cannot is not shipped.
/// </summary>
[TestFixture]
public class SyncRuleMappingMissingInputBehaviourApiTests
{
    private const int SyncRuleId = 1;
    private const int ObjectTypeId = 7;
    private const int TargetAttributeId = 20;

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private SynchronisationController _controller = null!;
    private SyncRuleMapping? _createdMapping;

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

        // An export Synchronisation Rule with one writable target attribute is the smallest shape that reaches
        // the source-building code the behaviour is set in.
        var syncRule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "Export Rule",
            Direction = SyncRuleDirection.Export,
            ConnectedSystemObjectTypeId = ObjectTypeId
        };
        var targetAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = TargetAttributeId,
            Name = "userPrincipalName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = ObjectTypeId, Name = "user" }
        };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(TargetAttributeId)).ReturnsAsync(targetAttribute);

        // Capture what the controller actually asked to be persisted, and hand the same object back as the
        // created mapping so the response DTO is built from it.
        _mockConnectedSystemRepo
            .Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()))
            .Callback<SyncRuleMapping>(m => _createdMapping = m)
            .Returns(Task.CompletedTask);
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingAsync(It.IsAny<int>()))
            .ReturnsAsync(() => _createdMapping);

        // The duplicate-target check (#1532) reads the Synchronisation Rule's existing mappings on every
        // mapping create; these tests exercise Missing Input Behaviour, so default the list to empty.
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>());

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
    public async Task CreateSyncRuleMappingAsync_ExpressionSourceWithMissingInputBehaviour_PersistsAndReturnsItAsync()
    {
        var result = await _controller.CreateSyncRuleMappingAsync(SyncRuleId, BuildRequest(MissingInputBehaviour.FailObject));

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        Assert.That(_createdMapping, Is.Not.Null);

        var dto = (SyncRuleMappingDto)((CreatedAtRouteResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_createdMapping!.Sources[0].MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.FailObject),
                "The behaviour must reach the persisted mapping source, not be dropped between DTO and entity");
            Assert.That(dto.Sources[0].MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.FailObject),
                "The response must report the behaviour, so a caller can read back what it set");
        }
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExpressionSourceWithoutMissingInputBehaviour_DefaultsToEvaluateAnywayAsync()
    {
        // Omission must leave existing callers' mappings behaving exactly as they did before this feature.
        var result = await _controller.CreateSyncRuleMappingAsync(SyncRuleId, BuildRequest(behaviour: null));

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        Assert.That(_createdMapping!.Sources[0].MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.EvaluateAnyway));
    }

    [Test]
    public void SyncRuleMappingSourceDto_FromEntity_CarriesTheBehaviour()
    {
        var dto = SyncRuleMappingSourceDto.FromEntity(new SyncRuleMappingSource
        {
            Id = 3,
            Order = 0,
            Expression = "mv[\"Display Name\"]",
            MissingInputBehaviour = MissingInputBehaviour.FailMapping
        });

        Assert.That(dto.MissingInputBehaviour, Is.EqualTo(MissingInputBehaviour.FailMapping));
    }

    /// <summary>
    /// The API serialises enums by member name and rejects integer ordinals (ApiJsonConfiguration), so these
    /// names are the wire contract that PowerShell's ValidateSet and the documentation both quote. Renaming a
    /// member is a breaking API change, not a refactor.
    /// </summary>
    [Test]
    public void MissingInputBehaviour_MemberNames_AreTheWireContract()
    {
        Assert.That(Enum.GetNames<MissingInputBehaviour>(),
            Is.EqualTo(new[] { "EvaluateAnyway", "ContributeNoValue", "FailMapping", "FailObject" }));
    }

    private static CreateSyncRuleMappingRequest BuildRequest(MissingInputBehaviour? behaviour)
    {
        return new CreateSyncRuleMappingRequest
        {
            TargetConnectedSystemAttributeId = TargetAttributeId,
            Sources =
            [
                new CreateSyncRuleMappingSourceRequest
                {
                    Order = 0,
                    Expression = "Lower(mv[\"Display Name\"]) + \"@corp.local\"",
                    MissingInputBehaviour = behaviour
                }
            ]
        };
    }
}
