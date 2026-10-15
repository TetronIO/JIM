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

    /// <summary>
    /// A PUT that leaves CaseSensitive out keeps the criterion's stored setting (#2051), as UpdateObjectMatchingRuleRequest
    /// does: Set-JIMScopingCriterion without -CaseSensitive used to reset an ignore-case criterion to case-sensitive.
    /// </summary>
    [Test]
    public async Task UpdateScopingCriterionAsync_CaseSensitiveOmitted_KeepsTheStoredSettingAsync()
    {
        var rule = BuildExportRuleWithCriterion();
        var criterion = rule.ObjectScopingCriteriaGroups[0].Criteria[0];
        criterion.CaseSensitive = false;
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(rule);

        await UpdateIgnoringPersistenceAsync(new CreateScopingCriterionRequest
        {
            MetaverseAttributeId = TextAttributeId, ComparisonType = "Equals", StringValue = "Sales"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.StringValue, Is.EqualTo("Sales"), "the update was applied");
            Assert.That(criterion.CaseSensitive, Is.False, "an omitted setting must not reset the stored one");
        }
    }

    [Test]
    public async Task UpdateScopingCriterionAsync_CaseSensitiveSupplied_AppliesItAsync()
    {
        var rule = BuildExportRuleWithCriterion();
        var criterion = rule.ObjectScopingCriteriaGroups[0].Criteria[0];
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(rule);

        await UpdateIgnoringPersistenceAsync(new CreateScopingCriterionRequest
        {
            MetaverseAttributeId = TextAttributeId, ComparisonType = "Equals", StringValue = "Sales", CaseSensitive = false
        });

        Assert.That(criterion.CaseSensitive, Is.False);
    }

    [Test]
    public async Task CreateScopingCriterionAsync_CaseSensitiveOmitted_RespectsCaseAsync()
    {
        var rule = BuildExportRuleWithCriterion();
        var group = rule.ObjectScopingCriteriaGroups[0];
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(rule);

        try
        {
            await _controller.CreateScopingCriterionAsync(SyncRuleId, GroupId, new CreateScopingCriterionRequest
            {
                MetaverseAttributeId = TextAttributeId, ComparisonType = "Equals", StringValue = "Finance"
            });
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            // The criterion has been added to the group before the save is attempted.
        }

        Assert.That(group.Criteria.Single(c => c.StringValue == "Finance").CaseSensitive, Is.True,
            "a condition is exact unless the request says otherwise");
    }

    // ── Expression criteria (#2054) ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task UpdateScopingCriterionAsync_Expression_ReplacesTheAttributeWithTheExpressionAsync()
    {
        var rule = BuildExportRuleWithCriterion();
        var criterion = rule.ObjectScopingCriteriaGroups[0].Criteria[0];
        criterion.MetaverseAttributeId = TextAttributeId;
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(rule);

        await UpdateIgnoringPersistenceAsync(new CreateScopingCriterionRequest
        {
            Expression = "Lower(mv[\"Department\"])", CompareAs = "Text", ComparisonType = "StartsWith", StringValue = "fin"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.Expression, Is.EqualTo("Lower(mv[\"Department\"])"));
            Assert.That(criterion.CompareAs, Is.EqualTo(AttributeDataType.Text));
            Assert.That(criterion.MetaverseAttribute, Is.Null);
            Assert.That(criterion.MetaverseAttributeId, Is.Null, "a stale id would make the criterion both");
            Assert.That(criterion.IsValid(), Is.True);
        }
    }

    [Test]
    public async Task UpdateScopingCriterionAsync_AttributeAfterAnExpression_ClearsTheExpressionAsync()
    {
        var rule = BuildExportRuleWithCriterion();
        var criterion = rule.ObjectScopingCriteriaGroups[0].Criteria[0];
        criterion.MetaverseAttribute = null;
        criterion.Expression = "Lower(mv[\"Department\"])";
        criterion.CompareAs = AttributeDataType.Text;
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(rule);

        await UpdateIgnoringPersistenceAsync(new CreateScopingCriterionRequest
        {
            MetaverseAttributeId = TextAttributeId, ComparisonType = "Equals", StringValue = "Sales"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.Expression, Is.Null);
            Assert.That(criterion.CompareAs, Is.Null);
            Assert.That(criterion.IsValid(), Is.True);
        }
    }

    [Test]
    public async Task UpdateScopingCriterionAsync_ExpressionComparedAsADateAgainstARelativeDate_IsAppliedAsync()
    {
        var rule = BuildExportRuleWithCriterion();
        var criterion = rule.ObjectScopingCriteriaGroups[0].Criteria[0];
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(rule);

        await UpdateIgnoringPersistenceAsync(new CreateScopingCriterionRequest
        {
            Expression = "ParseDate(mv[\"Department\"], \"yyyy-MM-dd\")", CompareAs = "DateTime", ComparisonType = "GreaterThan",
            ValueMode = "Relative", RelativeCount = 30, RelativeUnit = "Days", RelativeDirection = "Ago"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(criterion.ValueMode, Is.EqualTo(DateCriteriaValueMode.Relative));
            Assert.That(criterion.RelativeCount, Is.EqualTo(30));
        }
    }

    [TestCase(null, "Compare")]
    [TestCase("Reference", "Compare")]
    [TestCase("NotAType", "Compare")]
    public async Task UpdateScopingCriterionAsync_ExpressionWithoutAUsableCompareAs_ReturnsBadRequestAsync(string? compareAs, string expected)
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(BuildExportRuleWithCriterion());

        var result = await _controller.UpdateScopingCriterionAsync(SyncRuleId, GroupId, CriterionId, new CreateScopingCriterionRequest
        {
            Expression = "mv[\"Department\"]", CompareAs = compareAs, ComparisonType = "Equals", StringValue = "x"
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain(expected));
    }

    [Test]
    public async Task UpdateScopingCriterionAsync_ExpressionAndAnAttribute_ReturnsBadRequestAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(SyncRuleId)).ReturnsAsync(BuildExportRuleWithCriterion());

        var result = await _controller.UpdateScopingCriterionAsync(SyncRuleId, GroupId, CriterionId, new CreateScopingCriterionRequest
        {
            MetaverseAttributeId = TextAttributeId, Expression = "mv[\"Department\"]", CompareAs = "Text", ComparisonType = "Equals", StringValue = "x"
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public void SyncRuleScopingCriteriaDto_FromExpressionCriterion_DescribesTheExpressionAndItsType()
    {
        var dto = SyncRuleScopingCriteriaDto.FromEntity(new SyncRuleScopingCriteria
        {
            Expression = "Lower(mv[\"Department\"])", CompareAs = AttributeDataType.Text, ComparisonType = SearchComparisonType.Equals, StringValue = "x"
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Expression, Is.EqualTo("Lower(mv[\"Department\"])"));
            Assert.That(dto.CompareAs, Is.EqualTo("Text"));
            Assert.That(dto.AttributeDataType, Is.EqualTo("Text"));
            Assert.That(dto.Notice, Is.Null);
        }
    }

    [Test]
    public void SyncRuleScopingCriteriaDto_FromExpressionComparedAgainstARelativeDate_SaysItCostsAFullPass()
    {
        var dto = SyncRuleScopingCriteriaDto.FromEntity(new SyncRuleScopingCriteria
        {
            Expression = "ParseDate(mv[\"Start\"], \"yyyy-MM-dd\")", CompareAs = AttributeDataType.DateTime,
            ComparisonType = SearchComparisonType.GreaterThan, ValueMode = DateCriteriaValueMode.Relative,
            RelativeCount = 30, RelativeUnit = RelativeDateUnit.Days, RelativeDirection = RelativeDateDirection.Ago
        });

        Assert.That(dto.Notice, Does.Contain("every").And.Contain("Temporal Scope Reconciliation"));
    }

    /// <summary>
    /// Runs the update and inspects the criterion it applied the request to. Persisting the Synchronisation Rule is
    /// the application layer's concern and is covered elsewhere; this fixture's repository is not wired for it.
    /// </summary>
    private async Task UpdateIgnoringPersistenceAsync(CreateScopingCriterionRequest request)
    {
        try
        {
            await _controller.UpdateScopingCriterionAsync(SyncRuleId, GroupId, CriterionId, request);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            // The request has been applied to the criterion before the save is attempted.
        }
    }
}
