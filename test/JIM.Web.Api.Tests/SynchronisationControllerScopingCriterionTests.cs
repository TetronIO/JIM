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
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the scoping-criterion update (PUT) endpoint, focused on its routing, not-found handling and
/// relative-date validation (the paths that return before the Synchronisation Rule is persisted). The
/// relative resolution semantics are covered by ScopingEvaluationTests and RelativeDateResolverTests, and the
/// shared validation by RelativeDateCriterionValidationTests.
/// </summary>
[TestFixture]
public class SynchronisationControllerScopingCriterionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private SynchronisationController _controller = null!;

    private const int SyncRuleId = 1;
    private const int GroupId = 10;
    private const int CriterionId = 20;
    private const int TextAttributeId = 5;
    private const int DateAttributeId = 6;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);

        var application = new JimApplication(_mockRepository.Object);
        var expressionEvaluator = new DynamicExpressoEvaluator();
        var credentialProtection = new Mock<ICredentialProtectionService>();
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, application, expressionEvaluator, credentialProtection.Object);

        var apiKeyId = Guid.NewGuid();
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId, Name = "TestApiKey", KeyHash = "h", KeyPrefix = "t", IsEnabled = true, Created = DateTime.UtcNow
        });

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
    }

    private static SyncRule BuildExportRuleWithCriterion()
    {
        var textAttr = new MetaverseAttribute { Id = TextAttributeId, Name = "Department", Type = AttributeDataType.Text };
        var dateAttr = new MetaverseAttribute { Id = DateAttributeId, Name = "AccountExpiry", Type = AttributeDataType.DateTime };
        var rule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "Export Rule",
            Direction = SyncRuleDirection.Export,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", Attributes = new List<MetaverseAttribute> { textAttr, dateAttr } }
        };
        var group = new SyncRuleScopingCriteriaGroup { Id = GroupId, Type = SearchGroupType.All };
        group.Criteria.Add(new SyncRuleScopingCriteria { Id = CriterionId, MetaverseAttribute = textAttr, ComparisonType = SearchComparisonType.Equals, StringValue = "IT" });
        rule.ObjectScopingCriteriaGroups.Add(group);
        return rule;
    }

    [Test]
    public async Task UpdateScopingCriterionAsync_WithUnknownSyncRule_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync((SyncRule?)null);

        var result = await _controller.UpdateScopingCriterionAsync(SyncRuleId, GroupId, CriterionId,
            new CreateScopingCriterionRequest { MetaverseAttributeId = TextAttributeId, ComparisonType = "Equals", StringValue = "x" });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateScopingCriterionAsync_WithUnknownCriterion_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(BuildExportRuleWithCriterion());

        var result = await _controller.UpdateScopingCriterionAsync(SyncRuleId, GroupId, criterionId: 999,
            new CreateScopingCriterionRequest { MetaverseAttributeId = TextAttributeId, ComparisonType = "Equals", StringValue = "x" });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateScopingCriterionAsync_WithRelativeOnNonDateAttribute_ReturnsBadRequestAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(BuildExportRuleWithCriterion());

        var result = await _controller.UpdateScopingCriterionAsync(SyncRuleId, GroupId, CriterionId,
            new CreateScopingCriterionRequest
            {
                MetaverseAttributeId = TextAttributeId, // Text attribute
                ComparisonType = "Equals",
                ValueMode = "Relative",
                RelativeCount = 7,
                RelativeUnit = "Days",
                RelativeDirection = "FromNow"
            });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateScopingCriterionAsync_WithRelativeNegativeCount_ReturnsBadRequestAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(BuildExportRuleWithCriterion());

        var result = await _controller.UpdateScopingCriterionAsync(SyncRuleId, GroupId, CriterionId,
            new CreateScopingCriterionRequest
            {
                MetaverseAttributeId = DateAttributeId, // DateTime attribute
                ComparisonType = "LessThanOrEquals",
                ValueMode = "Relative",
                RelativeCount = -5,
                RelativeUnit = "Days",
                RelativeDirection = "Ago"
            });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }
}
