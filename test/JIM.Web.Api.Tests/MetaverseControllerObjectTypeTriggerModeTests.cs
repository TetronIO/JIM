// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using JIM.Web;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the DeletionTriggerMode surface on the Metaverse Object Type endpoints (#119):
/// read exposure on the detail DTO, create defaulting (omitted means the model's safe
/// AllSourcesDisconnect default), update semantics (omitted means unchanged), and wire-format
/// enforcement (string enum values only; unknown strings and integers fail deserialisation,
/// which the framework surfaces as a 400).
/// </summary>
[TestFixture]
public class MetaverseControllerObjectTypeTriggerModeTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<MetaverseController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;
    private JIM.Models.Security.ApiKey _testApiKey = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockLogger = new Mock<ILogger<MetaverseController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new MetaverseController(_mockLogger.Object, _application);

        // The controller's activity-creating paths require an attributable API key principal.
        // Mirror MetaverseControllerObjectTypeTests so the ApiKey overloads are taken.
        var apiKeyId = Guid.NewGuid();
        _testApiKey = new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(_testApiKey);

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

    #region helpers

    /// <summary>
    /// Wires up the mocks for a successful create: no name/plural-name clashes, capture of the
    /// entity passed to the repository, and re-retrieval of the captured entity by its new id.
    /// </summary>
    private Func<MetaverseObjectType?> SetUpSuccessfulCreate(string name, string pluralName, int newId)
    {
        MetaverseObjectType? captured = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(name, false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync(pluralName, false))
            .ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(o => { o.Id = newId; captured = o; })
            .Returns(Task.CompletedTask);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(newId, false))
            .ReturnsAsync(() => captured);
        return () => captured;
    }

    /// <summary>
    /// Wires up the mocks for a successful update of the supplied entity, capturing what is persisted.
    /// </summary>
    private Func<MetaverseObjectType?> SetUpSuccessfulUpdate(MetaverseObjectType objectType)
    {
        MetaverseObjectType? captured = null;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(objectType.Id, false))
            .ReturnsAsync(objectType);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()))
            .Callback<MetaverseObjectType>(ot => captured = ot)
            .Returns(Task.CompletedTask);
        return () => captured;
    }

    private static JsonSerializerOptions ConfiguredApiJsonOptions()
    {
        var options = new JsonSerializerOptions();
        ApiJsonConfiguration.Configure(options);
        return options;
    }

    #endregion

    #region GET detail

    [Test]
    public async Task GetObjectTypeAsync_WithAllSourcesTriggerMode_ReturnsModeInDto()
    {
        // AllSourcesDisconnect (enum value 1) is deliberately used here: an unmapped DTO would read
        // the enum's zero value (SpecificSourcesDisconnect), so this pins the FromEntity mapping.
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            DeletionTriggerConnectedSystemIds = new List<int> { 1 },
            Attributes = new List<MetaverseAttribute>()
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);

        var result = await _controller.GetObjectTypeAsync(1) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var dto = result!.Value as MetaverseObjectTypeDetailDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
    }

    [Test]
    public async Task GetObjectTypeAsync_WithSpecificSourcesTriggerMode_ReturnsModeInDto()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            DeletionTriggerConnectedSystemIds = new List<int> { 1 },
            Attributes = new List<MetaverseAttribute>()
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false))
            .ReturnsAsync(objectType);

        var result = await _controller.GetObjectTypeAsync(1) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var dto = result!.Value as MetaverseObjectTypeDetailDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect));
    }

    [Test]
    public void MetaverseObjectTypeDetailDto_SerialisedWithApiJsonPolicy_EmitsDeletionTriggerModeAsString()
    {
        // Built directly (not via FromEntity) so this test pins only the wire format: the mode must
        // serialise as the enum member name, consistent with every other enum the API exposes.
        var dto = new MetaverseObjectTypeDetailDto
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect
        };

        var json = JsonSerializer.Serialize(dto, ConfiguredApiJsonOptions());

        using var document = JsonDocument.Parse(json);
        var property = document.RootElement.GetProperty("DeletionTriggerMode");
        Assert.That(property.ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(property.GetString(), Is.EqualTo("SpecificSourcesDisconnect"));
    }

    #endregion

    #region Create

    [Test]
    public async Task CreateObjectTypeAsync_TriggerModeOmitted_PersistsAllSourcesDisconnect()
    {
        var captured = SetUpSuccessfulCreate("Device", "Devices", 42);

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Device",
            PluralName = "Devices"
            // DeletionTriggerMode deliberately omitted: the model's safe default must apply.
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedResult>());
        Assert.That(captured(), Is.Not.Null);
        Assert.That(captured()!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));

        var dto = ((CreatedResult)result).Value as MetaverseObjectTypeDetailDto;
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
    }

    [Test]
    public async Task CreateObjectTypeAsync_WithSpecificSourcesDisconnect_PersistsSpecificSourcesDisconnect()
    {
        var captured = SetUpSuccessfulCreate("Device", "Devices", 42);

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Device",
            PluralName = "Devices",
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedResult>());
        Assert.That(captured(), Is.Not.Null);
        Assert.That(captured()!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect));
    }

    [Test]
    public async Task CreateObjectTypeAsync_WithAllSourcesDisconnect_PersistsAllSourcesDisconnect()
    {
        var captured = SetUpSuccessfulCreate("Device", "Devices", 42);

        var request = new CreateMetaverseObjectTypeRequest
        {
            Name = "Device",
            PluralName = "Devices",
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect
        };

        var result = await _controller.CreateObjectTypeAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedResult>());
        Assert.That(captured(), Is.Not.Null);
        Assert.That(captured()!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
    }

    [Test]
    public void CreateRequest_UnknownDeletionTriggerModeString_FailsDeserialisation()
    {
        // Model binding surfaces this JsonException as a 400 with the offending property path,
        // consistent with every other enum-typed request DTO property (see ApiJsonConfiguration).
        const string json = "{\"Name\":\"Device\",\"PluralName\":\"Devices\",\"DeletionTriggerMode\":\"NotAMode\"}";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CreateMetaverseObjectTypeRequest>(json, ConfiguredApiJsonOptions()));
    }

    [Test]
    public void CreateRequest_IntegerDeletionTriggerMode_FailsDeserialisation()
    {
        // The API contract is string enum values only; numeric ordinals are rejected wire-wide.
        const string json = "{\"Name\":\"Device\",\"PluralName\":\"Devices\",\"DeletionTriggerMode\":1}";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CreateMetaverseObjectTypeRequest>(json, ConfiguredApiJsonOptions()));
    }

    #endregion

    #region Update

    [Test]
    public async Task UpdateObjectTypeAsync_TriggerModeOmitted_ExistingSpecificSourcesUnchanged()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect
        };
        var captured = SetUpSuccessfulUpdate(objectType);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionGracePeriod = TimeSpan.FromDays(7)
            // DeletionTriggerMode deliberately omitted: the stored mode must not change.
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(captured(), Is.Not.Null);
        Assert.That(captured()!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_TriggerModeOmitted_ExistingAllSourcesUnchanged()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect
        };
        var captured = SetUpSuccessfulUpdate(objectType);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionGracePeriod = TimeSpan.FromDays(7)
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(captured(), Is.Not.Null);
        Assert.That(captured()!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithAllSourcesDisconnect_AppliesMode()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect
        };
        var captured = SetUpSuccessfulUpdate(objectType);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(captured(), Is.Not.Null);
        Assert.That(captured()!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WithSpecificSourcesDisconnect_AppliesMode()
    {
        var objectType = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect
        };
        var captured = SetUpSuccessfulUpdate(objectType);

        var request = new UpdateMetaverseObjectTypeRequest
        {
            DeletionTriggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect
        };

        var result = await _controller.UpdateObjectTypeAsync(1, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(captured(), Is.Not.Null);
        Assert.That(captured()!.DeletionTriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect));
    }

    [Test]
    public void UpdateRequest_UnknownDeletionTriggerModeString_FailsDeserialisation()
    {
        const string json = "{\"DeletionTriggerMode\":\"EverySourceGone\"}";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<UpdateMetaverseObjectTypeRequest>(json, ConfiguredApiJsonOptions()));
    }

    [Test]
    public void UpdateRequest_IntegerDeletionTriggerMode_FailsDeserialisation()
    {
        const string json = "{\"DeletionTriggerMode\":0}";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<UpdateMetaverseObjectTypeRequest>(json, ConfiguredApiJsonOptions()));
    }

    #endregion
}
