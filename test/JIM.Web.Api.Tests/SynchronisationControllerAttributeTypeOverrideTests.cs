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
using JIM.Models.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
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
/// Covers overriding the data type schema discovery inferred for a Connected System attribute (#1354).
/// </summary>
/// <remarks>
/// This is what lets an Oracle deployment use the built-in numeric Metaverse Attributes. Oracle has a
/// single numeric type, so a <c>NUMBER(10)</c> column may be a whole number, a counter or a fractional
/// figure and the catalogue cannot say which; JIM infers the narrowest safe type and the administrator
/// corrects it where the estate means something the declaration does not state. Tested at the REST
/// layer because both PowerShell and the portal's automation callers go through it, so a portal-only
/// guard would be bypassable.
/// </remarks>
[TestFixture]
public class SynchronisationControllerAttributeTypeOverrideTests
{
    private const int ConnectedSystemId = 1;
    private const int ObjectTypeId = 7;
    private const int AttributeId = 20;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<SynchronisationController>> _mockLogger = null!;
    private Mock<ICredentialProtectionService> _mockCredentialProtection = null!;
    private IExpressionEvaluator _expressionEvaluator = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);

        // Unreferenced by default; the tests that care about the guard override this.
        _mockConnectedSystemRepo
            .Setup(r => r.IsObjectTypeAttributeBeingReferencedAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>()))
            .ReturnsAsync(false);

        _mockLogger = new Mock<ILogger<SynchronisationController>>();
        _mockCredentialProtection = new Mock<ICredentialProtectionService>();
        _expressionEvaluator = new DynamicExpressoEvaluator();
        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(_mockLogger.Object, _application, _expressionEvaluator, _mockCredentialProtection.Object);

        var apiKeyId = Guid.NewGuid();
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
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

        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_TypeOverrideOnASupportingConnector_IsAppliedAsync()
    {
        // The case this exists for: an Oracle NUMBER(10) read as a Decimal, corrected to a whole number so
        // it can flow into the built-in Employee Number Metaverse Attribute.
        Arrange(supportsUserSelectedAttributeTypes: true, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Type = AttributeDataType.Number });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Number));
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_TypeOverrideOnANonSupportingConnector_ReturnsBadRequestAsync()
    {
        // A directory states its own schema unambiguously, so there is nothing for an administrator to
        // correct and an override could only introduce a disagreement with the source.
        Arrange(supportsUserSelectedAttributeTypes: false, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Type = AttributeDataType.Number });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Decimal), "The attribute must not have been mutated before the rejection.");
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_TypeOverrideOnAReferencedAttribute_ReturnsBadRequestAsync()
    {
        // Values already imported were interpreted under the old type, and a Synchronisation Rule was
        // validated against it. Changing it underneath either is how a Connector Space starts disagreeing
        // with itself.
        Arrange(supportsUserSelectedAttributeTypes: true, out var attribute);
        _mockConnectedSystemRepo
            .Setup(r => r.IsObjectTypeAttributeBeingReferencedAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>()))
            .ReturnsAsync(true);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Type = AttributeDataType.Number });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Decimal));
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_TypeUnchangedOnAReferencedAttribute_IsAcceptedAsync()
    {
        // Restating the type an attribute already has changes nothing, so a script that sends the whole
        // attribute back is not punished for a field it did not intend to alter.
        Arrange(supportsUserSelectedAttributeTypes: true, out var attribute);
        _mockConnectedSystemRepo
            .Setup(r => r.IsObjectTypeAttributeBeingReferencedAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>()))
            .ReturnsAsync(true);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Type = AttributeDataType.Decimal });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Decimal));
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_TypeSetToNotSet_ReturnsBadRequestAsync()
    {
        // NotSet is the "schema discovery has not run" state. An attribute in it cannot be used in a
        // mapping at all, so accepting it would be a way to quietly break a working configuration.
        Arrange(supportsUserSelectedAttributeTypes: true, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Type = AttributeDataType.NotSet });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Decimal));
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_NoTypeSupplied_LeavesTheTypeAloneAsync()
    {
        // The regression guard: selecting an attribute must not disturb its type, and must not consult the
        // Connector capability at all.
        Arrange(supportsUserSelectedAttributeTypes: false, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Selected = true });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Decimal));
        Assert.That(attribute.Selected, Is.True);
    }

    [Test]
    public async Task BulkUpdateConnectedSystemAttributesAsync_CarryingATypeOverride_ReturnsBadRequestAsync()
    {
        // Refused rather than dropped. The bulk path applies its changes through a call that carries only
        // the selection flags, so silently ignoring the type would let a scripted build report success
        // having changed nothing.
        Arrange(supportsUserSelectedAttributeTypes: true, out _);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(ObjectTypeId)).ReturnsAsync(new ConnectedSystemObjectType
        {
            Id = ObjectTypeId,
            Name = "Person",
            ConnectedSystemId = ConnectedSystemId
        });

        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(ConnectedSystemId, ObjectTypeId,
            new BulkUpdateConnectedSystemAttributesRequest
            {
                Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
                {
                    [AttributeId] = new() { Type = AttributeDataType.Number }
                }
            });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    /// <summary>
    /// An Oracle-shaped attribute: a whole-number column that schema discovery could only describe as a
    /// Decimal, on a Connected System whose Connector declares the capability under test.
    /// </summary>
    private void Arrange(bool supportsUserSelectedAttributeTypes, out ConnectedSystemObjectTypeAttribute attribute)
    {
        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Oracle HR",
            ConnectorDefinition = new ConnectorDefinition
            {
                Id = 1,
                Name = "JIM SQL Connector",
                SupportsUserSelectedAttributeTypes = supportsUserSelectedAttributeTypes
            }
        };

        attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = AttributeId,
            Name = "EMPLOYEE_ID",
            Type = AttributeDataType.Decimal,
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Id = ObjectTypeId,
                Name = "Person",
                ConnectedSystemId = ConnectedSystemId
            }
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(AttributeId)).ReturnsAsync(attribute);
    }
}
