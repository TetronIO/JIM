// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Metaverse Object Type deletion, name-availability and rename/re-icon endpoints added in #376.
/// </summary>
[TestFixture]
public class MetaverseControllerObjectTypeDeletionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<MetaverseController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockLogger = new Mock<ILogger<MetaverseController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new MetaverseController(_mockLogger.Object, _application);

        var apiKeyId = Guid.NewGuid();
        var testApiKey = new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(testApiKey);

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
    }

    private static MetaverseObjectType ObjectType(int id = 5, string name = "Device", bool builtIn = false) =>
        new() { Id = id, Name = name, PluralName = name + "s", BuiltIn = builtIn };

    private void SetupType(MetaverseObjectType objectType) =>
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(objectType.Id, false)).ReturnsAsync(objectType);

    #region delete-preview

    [Test]
    public async Task GetObjectTypeDeletePreviewAsync_WithUnknownId_ReturnsNotFound()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(99, false)).ReturnsAsync((MetaverseObjectType?)null);

        var result = await _controller.GetObjectTypeDeletePreviewAsync(99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetObjectTypeDeletePreviewAsync_ReturnsImpact()
    {
        SetupType(ObjectType());
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(4);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync([]);

        var result = await _controller.GetObjectTypeDeletePreviewAsync(5) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var impact = result!.Value as ObjectTypeDeletionImpact;
        Assert.That(impact, Is.Not.Null);
        Assert.That(impact!.MetaverseObjectCount, Is.EqualTo(4));
        Assert.That(impact!.BlockedByObjects, Is.True);
    }

    #endregion

    #region delete

    [Test]
    public async Task DeleteObjectTypeAsync_WithBuiltIn_ReturnsBadRequest()
    {
        SetupType(ObjectType(id: 1, name: "User", builtIn: true));

        var result = await _controller.DeleteObjectTypeAsync(1);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockMetaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteObjectTypeAsync_WithLiveObjects_ReturnsConflictWithImpact()
    {
        SetupType(ObjectType());
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(120);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync([]);

        var result = await _controller.DeleteObjectTypeAsync(5) as ConflictObjectResult;

        Assert.That(result, Is.Not.Null);
        Assert.That((result!.Value as ObjectTypeDeletionImpact)!.BlockedByObjects, Is.True);
        _mockMetaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteObjectTypeAsync_WithSyncRules_ReturnsConflict()
    {
        SetupType(ObjectType());
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(0);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync(
            [new ObjectTypeReference { Kind = ObjectTypeReferenceKind.SynchronisationRule, Description = "Import Devices" }]);

        var result = await _controller.DeleteObjectTypeAsync(5) as ConflictObjectResult;

        Assert.That(result, Is.Not.Null);
        Assert.That((result!.Value as ObjectTypeDeletionImpact)!.BlockedBySynchronisationRules, Is.True);
        _mockMetaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteObjectTypeAsync_WithReferencesAndNoConfirmation_ReturnsBadRequest()
    {
        SetupType(ObjectType());
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(0);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync(
            [new ObjectTypeReference { Kind = ObjectTypeReferenceKind.PredefinedSearch, Description = "All Devices" }]);

        var result = await _controller.DeleteObjectTypeAsync(5, confirmationName: null);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockMetaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteObjectTypeAsync_WithReferencesAndMatchingConfirmation_Deletes()
    {
        SetupType(ObjectType());
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(0);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync(
            [new ObjectTypeReference { Kind = ObjectTypeReferenceKind.PredefinedSearch, Description = "All Devices" }]);
        _mockMetaverseRepo.Setup(r => r.DeleteMetaverseObjectTypeAsync(5)).Returns(Task.CompletedTask);

        var result = await _controller.DeleteObjectTypeAsync(5, confirmationName: "Device") as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        Assert.That((result!.Value as ObjectTypeDeletionImpact)!.Deleted, Is.True);
        _mockMetaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(5), Times.Once);
    }

    [Test]
    public async Task DeleteObjectTypeAsync_WithNoReferences_DeletesWithoutConfirmation()
    {
        SetupType(ObjectType());
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(0);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync([]);
        _mockMetaverseRepo.Setup(r => r.DeleteMetaverseObjectTypeAsync(5)).Returns(Task.CompletedTask);

        var result = await _controller.DeleteObjectTypeAsync(5) as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        _mockMetaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(5), Times.Once);
    }

    #endregion

    #region rename / re-icon

    [Test]
    public async Task UpdateObjectTypeAsync_RenamesCustomTypeWithUniqueName()
    {
        SetupType(ObjectType(name: "Device"));
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Gadget", false)).ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>())).Returns(Task.CompletedTask);

        var result = await _controller.UpdateObjectTypeAsync(5, new UpdateMetaverseObjectTypeRequest { Name = "Gadget" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockMetaverseRepo.Verify(r => r.UpdateMetaverseObjectTypeAsync(It.Is<MetaverseObjectType>(t => t.Name == "Gadget")), Times.Once);
    }

    [Test]
    public async Task UpdateObjectTypeAsync_RenameClash_ReturnsBadRequest()
    {
        SetupType(ObjectType(name: "Device"));
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Gadget", false)).ReturnsAsync(ObjectType(id: 9, name: "Gadget"));

        var result = await _controller.UpdateObjectTypeAsync(5, new UpdateMetaverseObjectTypeRequest { Name = "Gadget" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockMetaverseRepo.Verify(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public async Task UpdateObjectTypeAsync_RenameBuiltIn_ReturnsBadRequest()
    {
        SetupType(ObjectType(id: 1, name: "User", builtIn: true));

        var result = await _controller.UpdateObjectTypeAsync(1, new UpdateMetaverseObjectTypeRequest { Name = "Person" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockMetaverseRepo.Verify(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public async Task UpdateObjectTypeAsync_ChangeIconOnBuiltIn_ReturnsBadRequest()
    {
        SetupType(ObjectType(id: 1, name: "User", builtIn: true));

        var result = await _controller.UpdateObjectTypeAsync(1, new UpdateMetaverseObjectTypeRequest { Icon = "Devices" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateObjectTypeAsync_DeletionRuleOnBuiltIn_IsAllowed()
    {
        SetupType(ObjectType(id: 1, name: "User", builtIn: true));
        _mockMetaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>())).Returns(Task.CompletedTask);

        var result = await _controller.UpdateObjectTypeAsync(1, new UpdateMetaverseObjectTypeRequest
        {
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    #endregion

    #region name-availability

    [Test]
    public async Task GetObjectTypeNameAvailabilityAsync_ReportsBothFlags()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Gadget", false)).ReturnsAsync((MetaverseObjectType?)null);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Gadgets", false)).ReturnsAsync(ObjectType(id: 9, name: "Gadget"));

        var result = await _controller.GetObjectTypeNameAvailabilityAsync("Gadget", "Gadgets") as OkObjectResult;

        Assert.That(result, Is.Not.Null);
        var dto = result!.Value as MetaverseObjectTypeNameAvailabilityDto;
        Assert.That(dto!.NameAvailable, Is.True);
        Assert.That(dto!.PluralNameAvailable, Is.False);
    }

    [Test]
    public async Task GetObjectTypeNameAvailabilityAsync_WithNeitherSupplied_ReturnsBadRequest()
    {
        var result = await _controller.GetObjectTypeNameAvailabilityAsync();

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion
}
