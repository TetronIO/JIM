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
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Staging;
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
/// Unique Value Generation's Phase 3 REST surfaces (#242, Work Package A): creating and updating a generated
/// mapping over the REST API, and the sequence state / "Start again" / generated-values-for-Metaverse-Object
/// endpoints. Runs the real <see cref="ConnectedSystemServer"/> and <see cref="Application.UniqueValues.UniqueValueGenerationServer"/>
/// over an in-memory <see cref="ISyncRepository"/>, so <see cref="SyncRuleMappingGenerationValidator"/> and the
/// counter logic run for real, not mocked out.
/// </summary>
[TestFixture]
public class SyncRuleMappingGenerationApiTests
{
    private const int ImportRuleId = 1;
    private const int ObjectTypeId = 7;
    private const int MetaverseAttributeId = 5;
    private static readonly Guid UnknownMetaverseObjectId = Guid.NewGuid();

    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private InMemorySyncRepository _syncRepository = null!;
    private SynchronisationController _controller = null!;
    private MetaverseController _metaverseController = null!;
    private SyncRule _importRule = null!;
    private readonly Dictionary<int, SyncRuleMapping> _mappingsById = new();
    private int _nextMappingId = 100;

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
        mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        var metaverseAttribute = new MetaverseAttribute { Id = MetaverseAttributeId, Name = "Employee Number", Type = AttributeDataType.LongNumber };
        mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(MetaverseAttributeId)).ReturnsAsync(metaverseAttribute);

        var textAttribute = new MetaverseAttribute { Id = 6, Name = "Account Name", Type = AttributeDataType.Text };
        mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(6)).ReturnsAsync(textAttribute);

        // Every Metaverse Object id "exists" except the one the not-found test deliberately never registers.
        mockMetaverseRepo
            .Setup(r => r.GetMetaverseObjectHeaderAsync(It.IsAny<Guid>()))
            .Returns((Guid id) => Task.FromResult(id == UnknownMetaverseObjectId ? null : new MetaverseObjectHeader { Id = id }));

        _importRule = new SyncRule { Id = ImportRuleId, Name = "Import Rule", Direction = SyncRuleDirection.Import, ConnectedSystemObjectTypeId = ObjectTypeId };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(ImportRuleId)).ReturnsAsync(_importRule);

        _mockConnectedSystemRepo
            .Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()))
            .Returns((SyncRuleMapping m) =>
            {
                m.Id = _nextMappingId++;
                _mappingsById[m.Id] = m;
                return Task.CompletedTask;
            });
        _mockConnectedSystemRepo
            .Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()))
            .Returns(Task.CompletedTask);
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingAsync(It.IsAny<int>()))
            .Returns((int id) => Task.FromResult(_mappingsById.GetValueOrDefault(id)));
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingForUpdateAsync(It.IsAny<int>()))
            .Returns((int id) => Task.FromResult(_mappingsById.GetValueOrDefault(id)));
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>());
        // A sole contributor short-circuits AutoAssignImportMappingPriorityAsync before it reads anything else.
        _mockConnectedSystemRepo
            .Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>());

        var application = new JimApplication(mockRepository.Object, syncRepository: _syncRepository);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);
        _metaverseController = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, application);

        // Authenticated via API key, matching SyncRuleMappingUpdateApiTests: this avoids the interactive-user
        // claim path, which resolves the current user through ServiceSettingsServer and would need a further
        // repository mock this fixture has no use for.
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
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")) };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _metaverseController.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    // ---- Create: validation and generation object ----

    [Test]
    public async Task CreateSyncRuleMappingAsync_ValidSequenceGeneration_CreatesAGeneratedMappingAsync()
    {
        var request = new CreateSyncRuleMappingRequest
        {
            TargetMetaverseAttributeId = MetaverseAttributeId,
            Sources = [],
            Generation = new CreateSyncRuleMappingGenerationRequest
            {
                TokenKind = GeneratedValueTokenKind.Sequence,
                SequenceStart = 1000
            }
        };

        var result = await _controller.CreateSyncRuleMappingAsync(ImportRuleId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        var dto = (SyncRuleMappingDto)((CreatedAtRouteResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.SourceType, Is.EqualTo(nameof(SyncRuleMappingSourcesType.GeneratedMapping)));
            Assert.That(dto.Generation, Is.Not.Null);
            Assert.That(dto.Generation!.TokenKind, Is.EqualTo(GeneratedValueTokenKind.Sequence));
            Assert.That(dto.Generation.SequenceStart, Is.EqualTo(1000));
        }
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_OnlyIfTakenWithNoBaseExpression_ReturnsBadRequestAsync()
    {
        // The validator requires a base expression for OnlyIfTaken; the REST surface must return it as a 400,
        // not an unhandled exception.
        var request = new CreateSyncRuleMappingRequest
        {
            TargetMetaverseAttributeId = 6,
            Sources = [],
            Generation = new CreateSyncRuleMappingGenerationRequest { TokenKind = GeneratedValueTokenKind.OnlyIfTaken }
        };

        var result = await _controller.CreateSyncRuleMappingAsync(ImportRuleId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var body = (ApiErrorResponse)((BadRequestObjectResult)result).Value!;
        Assert.That(body.Message, Does.Contain("base expression"));
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_NumberTargetWithSequence_SucceedsAsync()
    {
        var request = new CreateSyncRuleMappingRequest
        {
            TargetMetaverseAttributeId = MetaverseAttributeId,
            Sources = [],
            Generation = new CreateSyncRuleMappingGenerationRequest { TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 1 }
        };

        var result = await _controller.CreateSyncRuleMappingAsync(ImportRuleId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_SequenceStartAboveExistingCounter_ReportsTheSkipAsync()
    {
        // Seed a counter for the attribute (as though an earlier, now-deleted, generated mapping had already
        // advanced it; the counter outlives any one flow, plan decision 3).
        await _syncRepository.ReserveGeneratedValueSequenceBlockAsync(MetaverseAttributeId, null, floor: 1, count: 5, increment: 1);
        // The counter now stands at 6.

        var request = new CreateSyncRuleMappingRequest
        {
            TargetMetaverseAttributeId = MetaverseAttributeId,
            Sources = [],
            Generation = new CreateSyncRuleMappingGenerationRequest { TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 500 }
        };

        var result = await _controller.CreateSyncRuleMappingAsync(ImportRuleId, request);

        var dto = (SyncRuleMappingDto)((CreatedAtRouteResult)result).Value!;
        Assert.That(dto.Generation!.SequenceSkippedAhead, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Generation.SequenceSkippedAhead!.From, Is.EqualTo(6));
            Assert.That(dto.Generation.SequenceSkippedAhead.To, Is.EqualTo(500));
        }
    }

    // ---- Update settings: generation object ----

    [Test]
    public async Task UpdateSyncRuleMappingAsync_GenerationSettingsOnAnOrdinaryMapping_ReturnsBadRequestAsync()
    {
        var mapping = new SyncRuleMapping
        {
            Id = 200,
            SyncRule = _importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 6, Name = "Account Name", Type = AttributeDataType.Text },
            TargetMetaverseAttributeId = 6,
            Sources = { new SyncRuleMappingSource { Id = 1, Order = 0, Expression = "cs[\"mail\"]" } }
        };
        _mappingsById[mapping.Id] = mapping;

        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, mapping.Id,
            new UpdateSyncRuleMappingRequest { Generation = new UpdateSyncRuleMappingGenerationRequest { SequenceStart = 500 } });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_RaisedSequenceStart_MovesTheCounterAndReportsItAsync()
    {
        var generation = new SyncRuleMappingGeneration { Id = 1, TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 1 };
        var mapping = new SyncRuleMapping
        {
            Id = 201,
            SyncRule = _importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = MetaverseAttributeId, Name = "Employee Number", Type = AttributeDataType.LongNumber },
            TargetMetaverseAttributeId = MetaverseAttributeId,
            Generation = generation
        };
        _mappingsById[mapping.Id] = mapping;
        await _syncRepository.ReserveGeneratedValueSequenceBlockAsync(MetaverseAttributeId, null, floor: 1, count: 5, increment: 1);
        // The counter now stands at 6.

        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, mapping.Id,
            new UpdateSyncRuleMappingRequest { Generation = new UpdateSyncRuleMappingGenerationRequest { SequenceStart = 500 } });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (SyncRuleMappingDto)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapping.Generation!.SequenceStart, Is.EqualTo(500));
            Assert.That(dto.Generation!.SequenceSkippedAhead, Is.Not.Null);
            Assert.That(dto.Generation.SequenceSkippedAhead!.From, Is.EqualTo(6));
            Assert.That(dto.Generation.SequenceSkippedAhead.To, Is.EqualTo(500));
        }
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_FixedWidthZero_ClearsItAsync()
    {
        var generation = new SyncRuleMappingGeneration { Id = 2, TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 1, FixedWidth = 4 };
        var mapping = new SyncRuleMapping
        {
            Id = 202,
            SyncRule = _importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = MetaverseAttributeId, Name = "Employee Number", Type = AttributeDataType.LongNumber },
            TargetMetaverseAttributeId = MetaverseAttributeId,
            Generation = generation
        };
        _mappingsById[mapping.Id] = mapping;

        var result = await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, mapping.Id,
            new UpdateSyncRuleMappingRequest { Generation = new UpdateSyncRuleMappingGenerationRequest { FixedWidth = 0 } });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(mapping.Generation!.FixedWidth, Is.Null);
    }

    // ---- Sequence state endpoint ----

    [Test]
    public async Task GetSyncRuleMappingSequenceStateAsync_SequenceMapping_ReturnsTheStateAsync()
    {
        var generation = new SyncRuleMappingGeneration { Id = 3, TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 1000 };
        var mapping = new SyncRuleMapping
        {
            Id = 203,
            SyncRule = _importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = MetaverseAttributeId, Name = "Employee Number", Type = AttributeDataType.LongNumber },
            TargetMetaverseAttributeId = MetaverseAttributeId,
            Generation = generation
        };
        _mappingsById[mapping.Id] = mapping;

        var result = await _controller.GetSyncRuleMappingSequenceStateAsync(ImportRuleId, mapping.Id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (GeneratedValueSequenceStateDto)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.NextNumber, Is.EqualTo(1000));
            Assert.That(dto.IsSeeded, Is.False);
            Assert.That(dto.AttributeName, Is.EqualTo("Employee Number"));
        }
    }

    [Test]
    public async Task GetSyncRuleMappingSequenceStateAsync_NotAGeneratedMapping_ReturnsNotFoundAsync()
    {
        var mapping = new SyncRuleMapping
        {
            Id = 204,
            SyncRule = _importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 6, Name = "Account Name", Type = AttributeDataType.Text },
            TargetMetaverseAttributeId = 6
        };
        _mappingsById[mapping.Id] = mapping;

        var result = await _controller.GetSyncRuleMappingSequenceStateAsync(ImportRuleId, mapping.Id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    // ---- Restart endpoint ----

    [Test]
    public async Task RestartSyncRuleMappingGeneratedValuesAsync_SequenceMapping_MovesTheCounterBackAsync()
    {
        var generation = new SyncRuleMappingGeneration { Id = 4, TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 1 };
        var mapping = new SyncRuleMapping
        {
            Id = 205,
            SyncRule = _importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = MetaverseAttributeId, Name = "Employee Number", Type = AttributeDataType.LongNumber },
            TargetMetaverseAttributeId = MetaverseAttributeId,
            Generation = generation
        };
        _mappingsById[mapping.Id] = mapping;
        await _syncRepository.ReserveGeneratedValueSequenceBlockAsync(MetaverseAttributeId, null, floor: 100, count: 50, increment: 1);
        // The counter now stands at 150.

        var result = await _controller.RestartSyncRuleMappingGeneratedValuesAsync(ImportRuleId, mapping.Id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (GeneratedValueRestartResultDto)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.CounterFrom, Is.EqualTo(150));
            Assert.That(dto.CounterTo, Is.EqualTo(1));
            Assert.That(dto.RetiredValuesForgotten, Is.Zero);
        }

        var sequence = await _syncRepository.GetGeneratedValueSequenceAsync(MetaverseAttributeId, null);
        Assert.That(sequence!.NextValue, Is.EqualTo(1));
    }

    [Test]
    public async Task RestartSyncRuleMappingGeneratedValuesAsync_NotAGeneratedMapping_ReturnsBadRequestAsync()
    {
        var mapping = new SyncRuleMapping
        {
            Id = 206,
            SyncRule = _importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 6, Name = "Account Name", Type = AttributeDataType.Text },
            TargetMetaverseAttributeId = 6
        };
        _mappingsById[mapping.Id] = mapping;

        var result = await _controller.RestartSyncRuleMappingGeneratedValuesAsync(ImportRuleId, mapping.Id);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    // ---- Generated values for a Metaverse Object ----

    [Test]
    public async Task GetGeneratedValuesForMetaverseObjectAsync_LiveAssignment_IsReturnedAsync()
    {
        var mvoId = Guid.NewGuid();
        var generation = new SyncRuleMappingGeneration { Id = 5, TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 1 };
        _syncRepository.SeedGeneratedValueAssignment(new JIM.Models.Transactional.GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = mvoId,
            MetaverseAttributeId = MetaverseAttributeId,
            Value = "1001",
            NormalisedValue = "1001",
            SyncRuleMappingGenerationId = generation.Id
        });

        var result = await _metaverseController.GetGeneratedValuesForMetaverseObjectAsync(mvoId);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dtos = ((OkObjectResult)result).Value as IEnumerable<GeneratedValueAssignmentHeaderDto>;
        Assert.That(dtos!.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetGeneratedValuesForMetaverseObjectAsync_UnknownObject_ReturnsNotFoundAsync()
    {
        var result = await _metaverseController.GetGeneratedValuesForMetaverseObjectAsync(UnknownMetaverseObjectId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }
}
