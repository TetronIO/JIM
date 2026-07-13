// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Metaverse Attribute CRUD, binding and safeguard endpoints (#377 Phase 2), driving the controller
/// over the real application server (<see cref="JIM.Application.Servers.MetaverseServer"/>) with a mocked repository.
/// Asserts the new cascade/values-block/confirmation-gate responses that replaced the old "references block deletion".
/// </summary>
[TestFixture]
public class MetaverseControllerAttributeTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IMetaverseRepository> _mv = null!;
    private Mock<IActivityRepository> _activity = null!;
    private Mock<IApiKeyRepository> _apiKeys = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _mv = new Mock<IMetaverseRepository>();
        _activity = new Mock<IActivityRepository>();
        _apiKeys = new Mock<IApiKeyRepository>();
        _repo.Setup(r => r.Metaverse).Returns(_mv.Object);
        _repo.Setup(r => r.Activity).Returns(_activity.Object);
        _repo.Setup(r => r.ApiKeys).Returns(_apiKeys.Object);

        _activity.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activity.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _application = new JimApplication(_repo.Object);
        _controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application);

        var apiKeyId = Guid.NewGuid();
        var apiKey = new JIM.Models.Security.ApiKey { Id = apiKeyId, Name = "TestApiKey", KeyHash = "h", KeyPrefix = "p", IsEnabled = true, Created = DateTime.UtcNow };
        _apiKeys.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(apiKey);

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };

        // Sensible defaults: unique names, no stored values, no references.
        _mv.Setup(r => r.IsMetaverseAttributeNameUniqueAsync(It.IsAny<string>(), It.IsAny<int?>())).ReturnsAsync(true);
        _mv.Setup(r => r.GetAttributeValueObjectCountAsync(It.IsAny<int>())).ReturnsAsync(0);
        _mv.Setup(r => r.GetAttributeValueObjectCountByTypeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(0);
        _mv.Setup(r => r.GetAttributeValueObjectCountsByTypeAsync(It.IsAny<int>())).ReturnsAsync([]);
        _mv.Setup(r => r.GetAttributeReferencesAsync(It.IsAny<int>())).ReturnsAsync([]);
        _mv.Setup(r => r.GetAttributeReferencesForObjectTypeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync([]);
        _mv.Setup(r => r.CascadeDeleteMetaverseAttributeAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        _mv.Setup(r => r.CascadeUnassignAttributeFromObjectTypeAsync(It.IsAny<int>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        _mv.Setup(r => r.CreateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>())).Returns(Task.CompletedTask);
        _mv.Setup(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>())).Returns(Task.CompletedTask);
        _mv.Setup(r => r.AddAttributeObjectTypeBindingAsync(It.IsAny<int>(), It.IsAny<int>())).Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    private MetaverseAttribute Attr(int id = 1, string name = "costCentre", bool builtIn = false, params MetaverseObjectType[] objectTypes)
    {
        var a = new MetaverseAttribute { Id = id, Name = name, BuiltIn = builtIn, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = objectTypes.ToList() };
        _mv.Setup(r => r.GetMetaverseAttributeAsync(id, It.IsAny<bool>())).ReturnsAsync(a);
        _mv.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(id, It.IsAny<bool>())).ReturnsAsync(a);
        return a;
    }

    #region Create

    [Test]
    public async Task CreateAttributeAsync_WithUniqueName_ReturnsCreatedAsync()
    {
        MetaverseAttribute? captured = null;
        _mv.Setup(r => r.CreateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>())).Callback<MetaverseAttribute>(a => { a.Id = 5; captured = a; });
        _mv.Setup(r => r.GetMetaverseAttributeAsync(5, It.IsAny<bool>())).ReturnsAsync(() => captured);

        var result = await _controller.CreateAttributeAsync(new CreateMetaverseAttributeRequest { Name = "costCentre", Type = AttributeDataType.Text });

        Assert.That(result, Is.InstanceOf<CreatedResult>());
    }

    [Test]
    public async Task CreateAttributeAsync_WithCaseInsensitiveClash_ReturnsConflictAsync()
    {
        _mv.Setup(r => r.IsMetaverseAttributeNameUniqueAsync("costCentre", null)).ReturnsAsync(false);

        var result = await _controller.CreateAttributeAsync(new CreateMetaverseAttributeRequest { Name = "costCentre", Type = AttributeDataType.Text });

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
        var error = ((ConflictObjectResult)result).Value as ApiErrorResponse;
        Assert.That(error!.Code, Is.EqualTo(ApiErrorCodes.Conflict));
    }

    [Test]
    public async Task CreateAttributeAsync_WithObjectTypeIds_BindsOnCreationAsync()
    {
        MetaverseAttribute? captured = null;
        _mv.Setup(r => r.GetMetaverseObjectTypeAsync(1, false)).ReturnsAsync(new MetaverseObjectType { Id = 1, Name = "User" });
        _mv.Setup(r => r.GetMetaverseObjectTypeAsync(2, false)).ReturnsAsync(new MetaverseObjectType { Id = 2, Name = "Group" });
        _mv.Setup(r => r.CreateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>())).Callback<MetaverseAttribute>(a => { a.Id = 5; captured = a; });
        _mv.Setup(r => r.GetMetaverseAttributeAsync(5, It.IsAny<bool>())).ReturnsAsync(() => captured);

        await _controller.CreateAttributeAsync(new CreateMetaverseAttributeRequest { Name = "costCentre", Type = AttributeDataType.Text, ObjectTypeIds = [1, 2] });

        Assert.That(captured!.MetaverseObjectTypes, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task CreateAttributeAsync_WithUnknownObjectTypeId_ReturnsBadRequestAsync()
    {
        _mv.Setup(r => r.GetMetaverseObjectTypeAsync(999, false)).ReturnsAsync((MetaverseObjectType?)null);

        var result = await _controller.CreateAttributeAsync(new CreateMetaverseAttributeRequest { Name = "costCentre", Type = AttributeDataType.Text, ObjectTypeIds = [999] });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region Name availability

    [Test]
    public async Task GetAttributeNameAvailabilityAsync_WhenTaken_ReturnsAvailableFalseAsync()
    {
        _mv.Setup(r => r.IsMetaverseAttributeNameUniqueAsync("costCentre", null)).ReturnsAsync(false);

        var result = await _controller.GetAttributeNameAvailabilityAsync("costCentre") as OkObjectResult;
        var dto = result!.Value as MetaverseAttributeNameAvailabilityDto;

        Assert.That(dto!.Available, Is.False);
        Assert.That(dto.Name, Is.EqualTo("costCentre"));
    }

    [Test]
    public async Task GetAttributeNameAvailabilityAsync_WhenAvailable_ReturnsTrueAsync()
    {
        var result = await _controller.GetAttributeNameAvailabilityAsync("newName", excludeId: 3) as OkObjectResult;
        var dto = result!.Value as MetaverseAttributeNameAvailabilityDto;

        Assert.That(dto!.Available, Is.True);
        _mv.Verify(r => r.IsMetaverseAttributeNameUniqueAsync("newName", 3), Times.Once);
    }

    [Test]
    public async Task GetAttributeNameAvailabilityAsync_WithEmptyName_ReturnsBadRequestAsync()
    {
        var result = await _controller.GetAttributeNameAvailabilityAsync("  ");
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region Update / rename

    [Test]
    public async Task UpdateAttributeAsync_Rename_ReturnsOkAsync()
    {
        Attr(1, "oldName");

        var result = await _controller.UpdateAttributeAsync(1, new UpdateMetaverseAttributeRequest { Name = "newName" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mv.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Once);
    }

    [Test]
    public async Task UpdateAttributeAsync_RenameToCaseInsensitiveClash_ReturnsConflictAsync()
    {
        Attr(1, "oldName");
        _mv.Setup(r => r.IsMetaverseAttributeNameUniqueAsync("CostCentre", 1)).ReturnsAsync(false);

        var result = await _controller.UpdateAttributeAsync(1, new UpdateMetaverseAttributeRequest { Name = "CostCentre" });

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_BuiltIn_ReturnsBadRequestAsync()
    {
        Attr(1, "Display Name", builtIn: true);

        var result = await _controller.UpdateAttributeAsync(1, new UpdateMetaverseAttributeRequest { Name = "newName" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mv.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    [Test]
    public async Task UpdateAttributeAsync_NotFound_ReturnsNotFoundAsync()
    {
        _mv.Setup(r => r.GetMetaverseAttributeAsync(999, It.IsAny<bool>())).ReturnsAsync((MetaverseAttribute?)null);

        var result = await _controller.UpdateAttributeAsync(999, new UpdateMetaverseAttributeRequest { Name = "newName" });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAttributeAsync_WithNoFields_ReturnsBadRequestAsync()
    {
        var result = await _controller.UpdateAttributeAsync(1, new UpdateMetaverseAttributeRequest());
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region Change schema (type / plurality)

    [Test]
    public async Task ChangeAttributeSchemaAsync_NoValues_ReturnsOkAsync()
    {
        Attr(1);

        var result = await _controller.ChangeAttributeSchemaAsync(1, new ChangeMetaverseAttributeSchemaRequest { Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.MultiValued });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mv.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Once);
    }

    [Test]
    public async Task ChangeAttributeSchemaAsync_WithStoredValues_ReturnsConflictWithImpactAsync()
    {
        Attr(1);
        _mv.Setup(r => r.GetAttributeValueObjectCountAsync(1)).ReturnsAsync(77);

        var result = await _controller.ChangeAttributeSchemaAsync(1, new ChangeMetaverseAttributeSchemaRequest { Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued });

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
        var impact = ((ConflictObjectResult)result).Value as AttributeSchemaChangeImpact;
        Assert.That(impact!.BlockedByValues, Is.True);
        Assert.That(impact.TotalObjectsWithValues, Is.EqualTo(77));
        _mv.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    [Test]
    public async Task ChangeAttributeSchemaAsync_BuiltIn_ReturnsBadRequestAsync()
    {
        Attr(1, "Display Name", builtIn: true);

        var result = await _controller.ChangeAttributeSchemaAsync(1, new ChangeMetaverseAttributeSchemaRequest { Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion

    #region Delete preview + execute

    [Test]
    public async Task GetAttributeDeletionPreviewAsync_ReturnsImpactAsync()
    {
        Attr(1);
        _mv.Setup(r => r.GetAttributeValueObjectCountsByTypeAsync(1)).ReturnsAsync(
        [
            new AttributeObjectTypeValueCount { MetaverseObjectTypeId = 1, MetaverseObjectTypeName = "User", ObjectCount = 3 }
        ]);

        var result = await _controller.GetAttributeDeletionPreviewAsync(1) as OkObjectResult;
        var impact = result!.Value as AttributeDeletionImpact;

        Assert.That(impact!.TotalObjectsWithValues, Is.EqualTo(3));
        Assert.That(impact.BlockedByValues, Is.True);
    }

    [Test]
    public async Task DeleteAttributeAsync_NoReferencesNoValues_ReturnsOkAndCascadesAsync()
    {
        Attr(1);

        var result = await _controller.DeleteAttributeAsync(1) as OkObjectResult;
        var impact = result!.Value as AttributeDeletionImpact;

        Assert.That(impact!.Deleted, Is.True);
        _mv.Verify(r => r.CascadeDeleteMetaverseAttributeAsync(1), Times.Once);
    }

    [Test]
    public async Task DeleteAttributeAsync_WithStoredValues_ReturnsConflictWithPerTypeCountsAsync()
    {
        Attr(1);
        _mv.Setup(r => r.GetAttributeValueObjectCountsByTypeAsync(1)).ReturnsAsync(
        [
            new AttributeObjectTypeValueCount { MetaverseObjectTypeId = 1, MetaverseObjectTypeName = "User", ObjectCount = 1200 },
            new AttributeObjectTypeValueCount { MetaverseObjectTypeId = 2, MetaverseObjectTypeName = "Group", ObjectCount = 323 }
        ]);

        var result = await _controller.DeleteAttributeAsync(1);

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
        var impact = ((ConflictObjectResult)result).Value as AttributeDeletionImpact;
        Assert.That(impact!.TotalObjectsWithValues, Is.EqualTo(1523));
        Assert.That(impact.ObjectTypeValueCounts, Has.Count.EqualTo(2));
        _mv.Verify(r => r.CascadeDeleteMetaverseAttributeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteAttributeAsync_WithReferencesAndNoConfirmation_ReturnsBadRequestAsync()
    {
        Attr(1, "costCentre");
        _mv.Setup(r => r.GetAttributeReferencesAsync(1)).ReturnsAsync(
        [
            new AttributeReference { Kind = AttributeReferenceKind.ImportAttributeFlow, Id = 10, Description = "Import Attribute Flow" }
        ]);

        var result = await _controller.DeleteAttributeAsync(1, confirmationName: null);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mv.Verify(r => r.CascadeDeleteMetaverseAttributeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteAttributeAsync_WithReferencesAndMismatchedConfirmation_ReturnsBadRequestAsync()
    {
        Attr(1, "costCentre");
        _mv.Setup(r => r.GetAttributeReferencesAsync(1)).ReturnsAsync(
        [
            new AttributeReference { Kind = AttributeReferenceKind.ImportAttributeFlow, Id = 10, Description = "Import Attribute Flow" }
        ]);

        var result = await _controller.DeleteAttributeAsync(1, confirmationName: "costcentre");

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task DeleteAttributeAsync_WithReferencesAndMatchingConfirmation_CascadesAndReturnsOkAsync()
    {
        Attr(1, "costCentre");
        _mv.Setup(r => r.GetAttributeReferencesAsync(1)).ReturnsAsync(
        [
            new AttributeReference { Kind = AttributeReferenceKind.ImportAttributeFlow, Id = 10, Description = "Import Attribute Flow" }
        ]);

        var result = await _controller.DeleteAttributeAsync(1, confirmationName: "costCentre") as OkObjectResult;
        var impact = result!.Value as AttributeDeletionImpact;

        Assert.That(impact!.Deleted, Is.True);
        Assert.That(impact.References, Has.Count.EqualTo(1));
        _mv.Verify(r => r.CascadeDeleteMetaverseAttributeAsync(1), Times.Once);
    }

    [Test]
    public async Task DeleteAttributeAsync_BuiltIn_ReturnsBadRequestAsync()
    {
        Attr(1, "Display Name", builtIn: true);

        var result = await _controller.DeleteAttributeAsync(1);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mv.Verify(r => r.CascadeDeleteMetaverseAttributeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteAttributeAsync_NotFound_ReturnsNotFoundAsync()
    {
        _mv.Setup(r => r.GetMetaverseAttributeAsync(999, It.IsAny<bool>())).ReturnsAsync((MetaverseAttribute?)null);

        var result = await _controller.DeleteAttributeAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region Bind

    [Test]
    public async Task BindAttributeToObjectTypeAsync_ReturnsOkAsync()
    {
        Attr(1);
        _mv.Setup(r => r.GetMetaverseObjectTypeAsync(2, false)).ReturnsAsync(new MetaverseObjectType { Id = 2, Name = "Group" });

        var result = await _controller.BindAttributeToObjectTypeAsync(1, 2);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mv.Verify(r => r.AddAttributeObjectTypeBindingAsync(1, 2), Times.Once);
    }

    [Test]
    public async Task BindAttributeToObjectTypeAsync_BuiltIn_ReturnsBadRequestAsync()
    {
        Attr(1, "Display Name", builtIn: true);
        _mv.Setup(r => r.GetMetaverseObjectTypeAsync(2, false)).ReturnsAsync(new MetaverseObjectType { Id = 2, Name = "Group" });

        var result = await _controller.BindAttributeToObjectTypeAsync(1, 2);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mv.Verify(r => r.AddAttributeObjectTypeBindingAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task BindAttributeToObjectTypeAsync_UnknownObjectType_ReturnsNotFoundAsync()
    {
        Attr(1);
        _mv.Setup(r => r.GetMetaverseObjectTypeAsync(2, false)).ReturnsAsync((MetaverseObjectType?)null);

        var result = await _controller.BindAttributeToObjectTypeAsync(1, 2);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region Unassign preview + execute

    private void SetupUnassign(int attributeId, int objectTypeId, bool builtIn, int objectsWithValues, List<AttributeReference> references, bool bound = true)
    {
        var boundTypes = bound ? new[] { new MetaverseObjectType { Id = objectTypeId, Name = "User" } } : [];
        Attr(attributeId, "costCentre", builtIn, boundTypes);
        _mv.Setup(r => r.GetMetaverseObjectTypeAsync(objectTypeId, false)).ReturnsAsync(new MetaverseObjectType { Id = objectTypeId, Name = "User" });
        _mv.Setup(r => r.GetAttributeValueObjectCountByTypeAsync(attributeId, objectTypeId)).ReturnsAsync(objectsWithValues);
        _mv.Setup(r => r.GetAttributeReferencesForObjectTypeAsync(attributeId, objectTypeId)).ReturnsAsync(references);
    }

    private static AttributeReference Binding(int typeId = 2) =>
        new() { Kind = AttributeReferenceKind.Binding, Id = typeId, MetaverseObjectTypeId = typeId, MetaverseObjectTypeName = "User", Description = "Object Type binding: User" };

    [Test]
    public async Task GetAttributeUnassignPreviewAsync_ReturnsImpactAsync()
    {
        SetupUnassign(1, 2, builtIn: false, objectsWithValues: 5, references: [Binding()]);

        var result = await _controller.GetAttributeUnassignPreviewAsync(1, 2) as OkObjectResult;
        var impact = result!.Value as AttributeUnassignImpact;

        Assert.That(impact!.BlockedByValues, Is.True);
        Assert.That(impact.WasBound, Is.True);
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_NotBound_ReturnsNotFoundAsync()
    {
        SetupUnassign(1, 2, builtIn: false, objectsWithValues: 0, references: [], bound: false);

        var result = await _controller.UnassignAttributeFromObjectTypeAsync(1, 2);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_WithValuesOfType_ReturnsConflictAsync()
    {
        SetupUnassign(1, 2, builtIn: false, objectsWithValues: 450, references: [Binding()]);

        var result = await _controller.UnassignAttributeFromObjectTypeAsync(1, 2);

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
        var impact = ((ConflictObjectResult)result).Value as AttributeUnassignImpact;
        Assert.That(impact!.ObjectsWithValues, Is.EqualTo(450));
        _mv.Verify(r => r.CascadeUnassignAttributeFromObjectTypeAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_WithReferencesNoConfirmation_ReturnsBadRequestAsync()
    {
        SetupUnassign(1, 2, builtIn: false, objectsWithValues: 0, references:
        [
            Binding(),
            new AttributeReference { Kind = AttributeReferenceKind.ImportAttributeFlow, Id = 10, Description = "Import Attribute Flow" }
        ]);

        var result = await _controller.UnassignAttributeFromObjectTypeAsync(1, 2, confirmationName: null);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mv.Verify(r => r.CascadeUnassignAttributeFromObjectTypeAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_BindingOnly_ReturnsOkAsync()
    {
        SetupUnassign(1, 2, builtIn: false, objectsWithValues: 0, references: [Binding()]);

        var result = await _controller.UnassignAttributeFromObjectTypeAsync(1, 2) as OkObjectResult;
        var impact = result!.Value as AttributeUnassignImpact;

        Assert.That(impact!.Unassigned, Is.True);
        _mv.Verify(r => r.CascadeUnassignAttributeFromObjectTypeAsync(1, 2), Times.Once);
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_BuiltIn_ReturnsBadRequestAsync()
    {
        SetupUnassign(1, 2, builtIn: true, objectsWithValues: 0, references: [Binding()]);

        var result = await _controller.UnassignAttributeFromObjectTypeAsync(1, 2);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion
}
