// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Security;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for ExampleDataController's Data Generation Template CRUD endpoints (issue #894).
/// The real ExampleDataServer runs against mocked repositories, so create/update fixtures must be
/// Validate()-clean template graphs; only the repository layer is mocked.
/// </summary>
[TestFixture]
public class ExampleDataControllerTemplateCrudTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IExampleDataRepository> _mockExampleDataRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<ILogger<ExampleDataController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private ExampleDataController _controller = null!;

    private MetaverseObjectType _userType = null!;
    private MetaverseAttribute _textAttribute = null!;
    private MetaverseAttribute _numberAttribute = null!;
    private MetaverseAttribute _referenceAttribute = null!;
    private ExampleDataSet _firstNamesDataSet = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockExampleDataRepo = new Mock<IExampleDataRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ExampleData).Returns(_mockExampleDataRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        // Every write path records a configuration-change Activity (see ExampleDataServer); stub the two calls it
        // always makes so these CRUD tests keep exercising just the repository mutation they assert on.
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _mockLogger = new Mock<ILogger<ExampleDataController>>();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new ExampleDataController(_mockLogger.Object, _application);

        // Authenticate as an API key so the base controller resolves a non-null initiating principal.
        var apiKeyId = Guid.NewGuid();
        var apiKey = new ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(apiKey);

        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        var httpContext = new DefaultHttpContext { User = principal };
        // The initiator triad helper reads the API key id from HttpContext.Items, where the API key middleware stashes it.
        httpContext.Items["ApiKeyId"] = apiKeyId;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Schema fixtures the resolution mocks hand back.
        _userType = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" };
        _textAttribute = new MetaverseAttribute
        {
            Id = 10,
            Name = "Display Name",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _numberAttribute = new MetaverseAttribute
        {
            Id = 11,
            Name = "Employee Number",
            Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _referenceAttribute = new MetaverseAttribute
        {
            Id = 12,
            Name = "Sponsor",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _firstNamesDataSet = new ExampleDataSet { Id = 5, Name = "First Names", Culture = "en-GB" };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, It.IsAny<bool>())).ReturnsAsync(_userType);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(10, It.IsAny<bool>())).ReturnsAsync(_textAttribute);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(11, It.IsAny<bool>())).ReturnsAsync(_numberAttribute);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(12, It.IsAny<bool>())).ReturnsAsync(_referenceAttribute);
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(5)).ReturnsAsync(_firstNamesDataSet);

        // No template already holds any requested name (the server's duplicate-name check).
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(It.IsAny<string>())).ReturnsAsync((ExampleDataTemplate?)null);
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    #region helpers

    /// <summary>
    /// A Validate()-clean create request: one Object Type ("User", 10 objects) with a patterned Text attribute,
    /// an Example-Data-Set-fed Text attribute, and a Reference attribute sourcing from the same Object Type
    /// (so id resolution can be proven to reuse one instance per id).
    /// </summary>
    private static CreateExampleDataTemplateRequest BuildValidCreateRequest()
    {
        return new CreateExampleDataTemplateRequest
        {
            Name = "HR Demo Data",
            ObjectTypes = new List<ExampleDataTemplateObjectTypeRequest>
            {
                new()
                {
                    MetaverseObjectTypeId = 1,
                    ObjectsToCreate = 10,
                    Attributes = new List<ExampleDataTemplateAttributeRequest>
                    {
                        new()
                        {
                            MetaverseAttributeId = 10,
                            ExampleDataSets = new List<ExampleDataTemplateDataSetInstanceRequest>
                            {
                                new() { ExampleDataSetId = 5, Order = 0 }
                            },
                            Pattern = "{0} [UniqueInt]"
                        },
                        new()
                        {
                            MetaverseAttributeId = 12,
                            ReferenceMetaverseObjectTypeIds = new List<int> { 1 }
                        }
                    }
                }
            },
            ChangeReason = "Initial template."
        };
    }

    private static ExampleDataTemplate BuildExistingTemplate(int id, string name, bool builtIn = false)
    {
        var template = new ExampleDataTemplate
        {
            Id = id,
            Name = name,
            BuiltIn = builtIn,
            Created = new DateTime(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc),
            CreatedByType = ActivityInitiatorType.User,
            CreatedById = Guid.NewGuid(),
            CreatedByName = "Original Author"
        };
        template.ObjectTypes.Add(new ExampleDataObjectType
        {
            Id = 100,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" },
            ObjectsToCreate = 25
        });
        return template;
    }

    #endregion

    #region create

    [Test]
    public async Task CreateTemplateAsync_ValidRequest_ReturnsCreatedWithDtoAsync()
    {
        var request = BuildValidCreateRequest();
        ExampleDataTemplate? created = null;
        // The server may persist through either template create repository method; wire both identically.
        _mockExampleDataRepo.Setup(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()))
            .Callback<ExampleDataTemplate>(t => { t.Id = 42; created = t; })
            .Returns(Task.CompletedTask);
        _mockExampleDataRepo.Setup(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()))
            .Callback<ExampleDataTemplate>(t => { t.Id = 42; created = t; })
            .Returns(Task.CompletedTask);
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(42)).ReturnsAsync(() => created);

        var result = await _controller.CreateTemplateAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
        var createdResult = (CreatedAtRouteResult)result;
        var dto = (ExampleDataTemplateDto)createdResult.Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(createdResult.RouteName, Is.EqualTo("GetExampleDataTemplate"));
            Assert.That(dto.Id, Is.EqualTo(42));
            Assert.That(dto.Name, Is.EqualTo("HR Demo Data"));
            Assert.That(dto.ObjectTypes, Has.Count.EqualTo(1));
        }
        Assert.That(created, Is.Not.Null, "The template should have been persisted through the repository.");
    }

    [Test]
    public async Task CreateTemplateAsync_ValidRequest_PassesResolvedGraphToRepositoryAsync()
    {
        var request = BuildValidCreateRequest();
        ExampleDataTemplate? created = null;
        // The server may persist through either template create repository method; wire both identically.
        _mockExampleDataRepo.Setup(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()))
            .Callback<ExampleDataTemplate>(t => { t.Id = 42; created = t; })
            .Returns(Task.CompletedTask);
        _mockExampleDataRepo.Setup(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()))
            .Callback<ExampleDataTemplate>(t => { t.Id = 42; created = t; })
            .Returns(Task.CompletedTask);
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(42)).ReturnsAsync(() => created);

        await _controller.CreateTemplateAsync(request);

        Assert.That(created, Is.Not.Null);
        var objectType = created!.ObjectTypes[0];
        var patternAttribute = objectType.TemplateAttributes[0];
        var referenceAttributeConfig = objectType.TemplateAttributes[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(created.BuiltIn, Is.False);
            Assert.That(objectType.MetaverseObjectType, Is.SameAs(_userType));
            Assert.That(objectType.ObjectsToCreate, Is.EqualTo(10));
            Assert.That(patternAttribute.MetaverseAttribute, Is.SameAs(_textAttribute));
            Assert.That(patternAttribute.Pattern, Is.EqualTo("{0} [UniqueInt]"));
            Assert.That(patternAttribute.ExampleDataSetInstances, Has.Count.EqualTo(1));
            Assert.That(patternAttribute.ExampleDataSetInstances[0].ExampleDataSet, Is.SameAs(_firstNamesDataSet));
            Assert.That(referenceAttributeConfig.MetaverseAttribute, Is.SameAs(_referenceAttribute));
            Assert.That(referenceAttributeConfig.ReferenceMetaverseObjectTypes, Is.Not.Null);
            Assert.That(referenceAttributeConfig.ReferenceMetaverseObjectTypes![0], Is.SameAs(_userType));
        }
        // The same Metaverse Object Type id appears twice in the request (the Object Type itself and the
        // reference attribute's source); resolution must reuse one instance rather than loading it twice.
        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectTypeAsync(1, It.IsAny<bool>()), Times.Once);
    }

    [Test]
    public async Task CreateTemplateAsync_UnknownMetaverseObjectTypeId_ReturnsNotFoundAsync()
    {
        var request = BuildValidCreateRequest();
        request.ObjectTypes[0].MetaverseObjectTypeId = 999;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(999, It.IsAny<bool>()))
            .ReturnsAsync((MetaverseObjectType?)null);

        var result = await _controller.CreateTemplateAsync(request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockExampleDataRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
        _mockExampleDataRepo.Verify(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
    }

    [Test]
    public async Task CreateTemplateAsync_UnknownMetaverseAttributeId_ReturnsNotFoundAsync()
    {
        var request = BuildValidCreateRequest();
        request.ObjectTypes[0].Attributes[0].MetaverseAttributeId = 888;
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(888, It.IsAny<bool>()))
            .ReturnsAsync((MetaverseAttribute?)null);

        var result = await _controller.CreateTemplateAsync(request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockExampleDataRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
        _mockExampleDataRepo.Verify(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
    }

    [Test]
    public async Task CreateTemplateAsync_UnknownConnectedSystemAttributeId_ReturnsNotFoundAsync()
    {
        var request = BuildValidCreateRequest();
        request.ObjectTypes[0].Attributes[0].MetaverseAttributeId = null;
        request.ObjectTypes[0].Attributes[0].ConnectedSystemObjectTypeAttributeId = 777;
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(777))
            .ReturnsAsync((JIM.Models.Staging.ConnectedSystemObjectTypeAttribute?)null);

        var result = await _controller.CreateTemplateAsync(request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockExampleDataRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
        _mockExampleDataRepo.Verify(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
    }

    [Test]
    public async Task CreateTemplateAsync_UnknownExampleDataSetId_ReturnsNotFoundAsync()
    {
        var request = BuildValidCreateRequest();
        request.ObjectTypes[0].Attributes[0].ExampleDataSets![0].ExampleDataSetId = 999;
        _mockExampleDataRepo.Setup(r => r.GetExampleDataSetAsync(999)).ReturnsAsync((ExampleDataSet?)null);

        var result = await _controller.CreateTemplateAsync(request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockExampleDataRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
        _mockExampleDataRepo.Verify(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
    }

    [Test]
    public async Task CreateTemplateAsync_PatternOnNumberAttribute_ReturnsBadRequestAsync()
    {
        // Pattern is only valid on Text attributes; the server's Validate() call must reject this with a 400.
        var request = BuildValidCreateRequest();
        request.ObjectTypes[0].Attributes = new List<ExampleDataTemplateAttributeRequest>
        {
            new() { MetaverseAttributeId = 11, Pattern = "EMP-[UniqueInt]" }
        };

        var result = await _controller.CreateTemplateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CreateTemplateAsync_DuplicateName_ReturnsConflictAsync()
    {
        var request = BuildValidCreateRequest();
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync("HR Demo Data"))
            .ReturnsAsync(BuildExistingTemplate(7, "HR Demo Data"));

        var result = await _controller.CreateTemplateAsync(request);

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
        _mockExampleDataRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
        _mockExampleDataRepo.Verify(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
    }

    [Test]
    public async Task CreateTemplateAsync_InvalidComparisonType_ReturnsBadRequestAsync()
    {
        var request = BuildValidCreateRequest();
        request.ObjectTypes[0].Attributes[0].AttributeDependency = new ExampleDataTemplateAttributeDependencyRequest
        {
            MetaverseAttributeId = 10,
            ComparisonType = "Wibble",
            StringValue = "x"
        };

        var result = await _controller.CreateTemplateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockExampleDataRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
        _mockExampleDataRepo.Verify(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
    }

    [Test]
    public async Task CreateTemplateAsync_NoIdentifiablePrincipal_ReturnsUnauthorisedAsync()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await _controller.CreateTemplateAsync(BuildValidCreateRequest());

        Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
        _mockExampleDataRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
        _mockExampleDataRepo.Verify(r => r.CreateTemplateGraphAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
    }

    #endregion

    #region update

    [Test]
    public async Task UpdateTemplateAsync_ObjectTypesSupplied_ReplacesGraphAndReturnsOkAsync()
    {
        var existing = BuildExistingTemplate(3, "Old Name");
        var current = existing;
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(3)).ReturnsAsync(() => current);

        ExampleDataTemplate? updated = null;
        bool? replaceObjectTypes = null;
        _mockExampleDataRepo.Setup(r => r.UpdateTemplateAsync(It.IsAny<ExampleDataTemplate>(), It.IsAny<bool>()))
            .Callback<ExampleDataTemplate, bool>((t, replace) => { updated = t; current = t; replaceObjectTypes = replace; })
            .Returns(Task.CompletedTask);

        var request = new UpdateExampleDataTemplateRequest
        {
            Name = "New Name",
            ObjectTypes = new List<ExampleDataTemplateObjectTypeRequest>
            {
                new()
                {
                    MetaverseObjectTypeId = 1,
                    ObjectsToCreate = 50,
                    Attributes = new List<ExampleDataTemplateAttributeRequest>
                    {
                        new() { MetaverseAttributeId = 10, Pattern = "user[UniqueInt]" }
                    }
                }
            },
            ChangeReason = "Reshaped the template."
        };

        var result = await _controller.UpdateTemplateAsync(3, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var dto = (ExampleDataTemplateDto)((OkObjectResult)result).Value!;
        Assert.That(updated, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Name, Is.EqualTo("New Name"));
            Assert.That(updated!.Id, Is.EqualTo(3));
            Assert.That(updated!.BuiltIn, Is.False);
            Assert.That(updated!.Created, Is.EqualTo(existing.Created));
            Assert.That(updated!.CreatedByName, Is.EqualTo("Original Author"));
            // ObjectTypes were supplied, so the request's graph replaces the existing one entirely.
            Assert.That(updated!.ObjectTypes, Has.Count.EqualTo(1));
            Assert.That(updated!.ObjectTypes[0].MetaverseObjectType, Is.SameAs(_userType));
            Assert.That(updated!.ObjectTypes[0].ObjectsToCreate, Is.EqualTo(50));
            Assert.That(replaceObjectTypes, Is.True);
        }
    }

    [Test]
    public async Task UpdateTemplateAsync_ObjectTypesNull_RenamesWithoutTouchingGraphAsync()
    {
        var existing = BuildExistingTemplate(3, "Old Name");
        var current = existing;
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(3)).ReturnsAsync(() => current);

        ExampleDataTemplate? updated = null;
        bool? replaceObjectTypes = null;
        _mockExampleDataRepo.Setup(r => r.UpdateTemplateAsync(It.IsAny<ExampleDataTemplate>(), It.IsAny<bool>()))
            .Callback<ExampleDataTemplate, bool>((t, replace) => { updated = t; current = t; replaceObjectTypes = replace; })
            .Returns(Task.CompletedTask);

        var request = new UpdateExampleDataTemplateRequest { Name = "New Name" };

        var result = await _controller.UpdateTemplateAsync(3, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(updated, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated!.Name, Is.EqualTo("New Name"));
            // A scalar-only rename passes no Object Types; the existing graph must be left untouched.
            Assert.That(updated!.ObjectTypes, Is.Empty);
            Assert.That(replaceObjectTypes, Is.False);
        }
    }

    [Test]
    public async Task UpdateTemplateAsync_UnknownId_ReturnsNotFoundAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(999)).ReturnsAsync((ExampleDataTemplate?)null);

        var result = await _controller.UpdateTemplateAsync(999, new UpdateExampleDataTemplateRequest { Name = "New Name" });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockExampleDataRepo.Verify(r => r.UpdateTemplateAsync(It.IsAny<ExampleDataTemplate>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task UpdateTemplateAsync_BuiltIn_ReturnsBadRequestAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(1)).ReturnsAsync(BuildExistingTemplate(1, "Built-in Template", builtIn: true));

        var result = await _controller.UpdateTemplateAsync(1, new UpdateExampleDataTemplateRequest { Name = "New Name" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockExampleDataRepo.Verify(r => r.UpdateTemplateAsync(It.IsAny<ExampleDataTemplate>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task UpdateTemplateAsync_DuplicateName_ReturnsConflictAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(3)).ReturnsAsync(BuildExistingTemplate(3, "Old Name"));
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync("Taken Name")).ReturnsAsync(BuildExistingTemplate(8, "Taken Name"));

        var result = await _controller.UpdateTemplateAsync(3, new UpdateExampleDataTemplateRequest { Name = "Taken Name" });

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
        _mockExampleDataRepo.Verify(r => r.UpdateTemplateAsync(It.IsAny<ExampleDataTemplate>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task UpdateTemplateAsync_UnknownMetaverseObjectTypeId_ReturnsNotFoundAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(3)).ReturnsAsync(BuildExistingTemplate(3, "Old Name"));
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(999, It.IsAny<bool>()))
            .ReturnsAsync((MetaverseObjectType?)null);

        var request = new UpdateExampleDataTemplateRequest
        {
            ObjectTypes = new List<ExampleDataTemplateObjectTypeRequest>
            {
                new() { MetaverseObjectTypeId = 999, ObjectsToCreate = 5 }
            }
        };

        var result = await _controller.UpdateTemplateAsync(3, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockExampleDataRepo.Verify(r => r.UpdateTemplateAsync(It.IsAny<ExampleDataTemplate>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion

    #region delete

    [Test]
    public async Task DeleteTemplateAsync_ValidRequest_DeletesAndReturnsNoContentAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(4)).ReturnsAsync(BuildExistingTemplate(4, "Custom Template"));

        var result = await _controller.DeleteTemplateAsync(4, "No longer needed.");

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        _mockExampleDataRepo.Verify(r => r.DeleteTemplateAsync(4), Times.Once);
    }

    [Test]
    public async Task DeleteTemplateAsync_UnknownId_ReturnsNotFoundAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(999)).ReturnsAsync((ExampleDataTemplate?)null);

        var result = await _controller.DeleteTemplateAsync(999);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockExampleDataRepo.Verify(r => r.DeleteTemplateAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task DeleteTemplateAsync_BuiltIn_ReturnsBadRequestAsync()
    {
        _mockExampleDataRepo.Setup(r => r.GetTemplateAsync(1)).ReturnsAsync(BuildExistingTemplate(1, "Built-in Template", builtIn: true));

        var result = await _controller.DeleteTemplateAsync(1);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockExampleDataRepo.Verify(r => r.DeleteTemplateAsync(It.IsAny<int>()), Times.Never);
    }

    #endregion
}
