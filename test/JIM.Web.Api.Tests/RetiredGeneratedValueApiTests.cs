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
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.TestSupport;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The retired values register's REST surface (Unique Value Generation, #242, Phase 6): reading an attribute's
/// retired values, Metaverse and Connected System alike, with search and paging; the retired count a generated
/// mapping carries; and "Start again" reporting how many it forgot. Runs the real application servers over an
/// in-memory <see cref="ISyncRepository"/>.
/// </summary>
[TestFixture]
public class RetiredGeneratedValueApiTests
{
    private const int ImportRuleId = 1;
    private const int MetaverseAttributeId = 6;
    private const int ConnectedSystemId = 3;
    private const int ObjectTypeId = 7;
    private const int ConnectedSystemAttributeId = 70;

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private InMemorySyncRepository _syncRepository = null!;
    private SynchronisationController _controller = null!;
    private MetaverseController _metaverseController = null!;
    private SyncRuleMapping _generatedMapping = null!;

    [SetUp]
    public void SetUp()
    {
        var mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        var mockMetaverseRepo = new Mock<IMetaverseRepository>();
        var mockActivityRepo = new Mock<IActivityRepository>();
        var mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _syncRepository = new InMemorySyncRepository();
        mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        mockRepository.Setup(r => r.Metaverse).Returns(mockMetaverseRepo.Object);
        mockRepository.Setup(r => r.Activity).Returns(mockActivityRepo.Object);
        mockRepository.Setup(r => r.ApiKeys).Returns(mockApiKeyRepo.Object);
        mockRepository.Setup(r => r.Sync).Returns(_syncRepository);
        mockRepository.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var accountName = new MetaverseAttribute { Id = MetaverseAttributeId, Name = "Account Name", Type = AttributeDataType.Text };
        mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(MetaverseAttributeId)).ReturnsAsync(accountName);

        var system = new ConnectedSystem { Id = ConnectedSystemId, Name = "Directory" };
        var objectType = new ConnectedSystemObjectType { Id = ObjectTypeId, Name = "user", ConnectedSystemId = ConnectedSystemId, ConnectedSystem = system };
        var loginName = new ConnectedSystemObjectTypeAttribute { Id = ConnectedSystemAttributeId, Name = "loginName", ConnectedSystemObjectType = objectType };
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId)).ReturnsAsync(system);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(ConnectedSystemAttributeId)).ReturnsAsync(loginName);

        var importRule = new SyncRule { Id = ImportRuleId, Name = "HR Import", Direction = SyncRuleDirection.Import, ConnectedSystemObjectTypeId = ObjectTypeId };
        _generatedMapping = new SyncRuleMapping
        {
            Id = 100,
            SyncRule = importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttributeId = MetaverseAttributeId,
            TargetMetaverseAttribute = accountName,
            Generation = new SyncRuleMappingGeneration { Id = 50, TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 1 }
        };
        var plainMapping = new SyncRuleMapping { Id = 101, SyncRule = importRule, SyncRuleId = ImportRuleId, TargetMetaverseAttributeId = 9 };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(ImportRuleId)).ReturnsAsync(importRule);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingsAsync(ImportRuleId)).ReturnsAsync([_generatedMapping, plainMapping]);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleMappingAsync(_generatedMapping.Id)).ReturnsAsync(_generatedMapping);

        var application = new JimApplication(mockRepository.Object, syncRepository: _syncRepository);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);
        _metaverseController = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, application);

        var apiKeyId = Guid.NewGuid();
        mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId, Name = "TestApiKey", KeyHash = "test-hash", KeyPrefix = "test", IsEnabled = true, Created = DateTime.UtcNow
        });
        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")) };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _metaverseController.ControllerContext = new ControllerContext { HttpContext = httpContext };

        for (var i = 1; i <= 3; i++)
        {
            _syncRepository.SeedRetiredGeneratedValue(new RetiredGeneratedValue
            {
                MetaverseAttributeId = MetaverseAttributeId,
                Value = $"fenwick{i}",
                RetiredAt = DateTime.UtcNow.AddDays(-i),
                Reason = RetiredGeneratedValueReason.ObjectDeleted,
                FromObjectDisplayName = $"Marisol Fenwick {i}",
                FromObjectId = Guid.NewGuid()
            });
        }
        _syncRepository.SeedRetiredGeneratedValue(new RetiredGeneratedValue
        {
            MetaverseAttributeId = MetaverseAttributeId,
            Value = "j.okafor",
            RetiredAt = DateTime.UtcNow.AddDays(-10),
            Reason = RetiredGeneratedValueReason.Superseded,
            FromObjectDisplayName = "Jide Okafor"
        });
        _syncRepository.SeedRetiredGeneratedValue(new RetiredGeneratedValue
        {
            ConnectedSystemObjectTypeAttributeId = ConnectedSystemAttributeId,
            Value = "jbloggs",
            RetiredAt = DateTime.UtcNow,
            Reason = RetiredGeneratedValueReason.Recalled
        });
    }

    [Test]
    public async Task GetRetiredGeneratedValuesForMetaverseAttributeAsync_ReturnsTheAttributesValuesNewestFirstWithTheirReasonsAsync()
    {
        var result = await _metaverseController.GetRetiredGeneratedValuesForMetaverseAttributeAsync(MetaverseAttributeId, new PaginationRequest { Page = 1, PageSize = 25 });

        var page = (PaginatedResponse<RetiredGeneratedValueDto>)((OkObjectResult)result).Value!;
        var items = page.Items.ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalCount, Is.EqualTo(4));
            Assert.That(items.Select(i => i.Value), Is.EqualTo(new[] { "fenwick1", "fenwick2", "fenwick3", "j.okafor" }));
            Assert.That(items[3].Reason, Is.EqualTo(RetiredGeneratedValueReason.Superseded));
            Assert.That(items[0].FromObjectDisplayName, Is.EqualTo("Marisol Fenwick 1"));
            Assert.That(items.All(i => i.MetaverseAttributeId == MetaverseAttributeId), Is.True, "only this attribute's register");
        }
    }

    [Test]
    public async Task GetRetiredGeneratedValuesForMetaverseAttributeAsync_SearchAndPage_NarrowsAndPagesTheMatchesAsync()
    {
        var result = await _metaverseController.GetRetiredGeneratedValuesForMetaverseAttributeAsync(
            MetaverseAttributeId, new PaginationRequest { Page = 2, PageSize = 2 }, search: "FENWICK");

        var page = (PaginatedResponse<RetiredGeneratedValueDto>)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalCount, Is.EqualTo(3));
            Assert.That(page.Items.Select(i => i.Value), Is.EqualTo(new[] { "fenwick3" }));
            Assert.That(page.Page, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task GetRetiredGeneratedValuesForMetaverseAttributeAsync_UnknownAttribute_ReturnsNotFoundAsync()
    {
        var result = await _metaverseController.GetRetiredGeneratedValuesForMetaverseAttributeAsync(999, new PaginationRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetRetiredGeneratedValuesForConnectedSystemAttributeAsync_ReturnsTheExportAttributesValuesAsync()
    {
        var result = await _controller.GetRetiredGeneratedValuesForConnectedSystemAttributeAsync(
            ConnectedSystemId, ObjectTypeId, ConnectedSystemAttributeId, new PaginationRequest());

        var page = (PaginatedResponse<RetiredGeneratedValueDto>)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Items.Select(i => i.Value), Is.EqualTo(new[] { "jbloggs" }));
            Assert.That(page.Items.Single().Reason, Is.EqualTo(RetiredGeneratedValueReason.Recalled));
            Assert.That(page.Items.Single().ConnectedSystemObjectTypeAttributeId, Is.EqualTo(ConnectedSystemAttributeId));
        }
    }

    [Test]
    public async Task GetRetiredGeneratedValuesForConnectedSystemAttributeAsync_AttributeOfAnotherObjectType_ReturnsNotFoundAsync()
    {
        var result = await _controller.GetRetiredGeneratedValuesForConnectedSystemAttributeAsync(
            ConnectedSystemId, ObjectTypeId + 1, ConnectedSystemAttributeId, new PaginationRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingsAsync_GeneratedMapping_CarriesItsAttributesRetiredCountAsync()
    {
        var result = await _controller.GetSyncRuleMappingsAsync(ImportRuleId);

        var mappings = ((IEnumerable<SyncRuleMappingDto>)((OkObjectResult)result).Value!).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mappings.Single(m => m.Id == _generatedMapping.Id).Generation!.RetiredValueCount, Is.EqualTo(4));
            Assert.That(mappings.Single(m => m.Id == 101).Generation, Is.Null);
        }
    }

    [Test]
    public async Task GetSyncRuleMappingAsync_GeneratedMapping_CarriesItsAttributesRetiredCountAsync()
    {
        var result = await _controller.GetSyncRuleMappingAsync(ImportRuleId, _generatedMapping.Id);

        var mapping = (SyncRuleMappingDto)((OkObjectResult)result).Value!;
        Assert.That(mapping.Generation!.RetiredValueCount, Is.EqualTo(4));
    }

    [Test]
    public async Task RestartSyncRuleMappingGeneratedValuesAsync_Sequence_ReportsHowManyRetiredValuesItForgotAsync()
    {
        var result = await _controller.RestartSyncRuleMappingGeneratedValuesAsync(ImportRuleId, _generatedMapping.Id);

        var dto = (GeneratedValueRestartResultDto)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.RetiredValuesForgotten, Is.EqualTo(4));
            Assert.That(_syncRepository.RetiredGeneratedValues.Select(r => r.Value), Is.EqualTo(new[] { "jbloggs" }),
                "only the target attribute's register is forgotten");
        }
    }
}
