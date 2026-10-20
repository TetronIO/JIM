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
/// Covers an administrator saying whether a Connected System attribute holds one value or several (#2186) through the
/// REST API, which PowerShell also uses. Tested at the REST layer because a portal-only guard would be bypassable.
/// </summary>
[TestFixture]
public class SynchronisationControllerAttributePluralityOverrideTests
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
        // Nothing reads any attribute, and every attribute is stored unselected, unless a test says otherwise (#2265).
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeReaderCandidatesAsync()).ReturnsAsync(() => []);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeSelectionStatesAsync(It.IsAny<int>())).ReturnsAsync(() => []);
        var attributeReaderExampleData = new Mock<IExampleDataRepository>();
        attributeReaderExampleData.Setup(r => r.GetAttributeReaderCandidatesAsync()).ReturnsAsync(() => []);
        _mockRepository.Setup(r => r.ExampleData).Returns(attributeReaderExampleData.Object);
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
    public async Task UpdateConnectedSystemAttributeAsync_PluralityOnASupportingConnector_IsAppliedAndReportedAsTheAdministratorsAsync()
    {
        Arrange(supportsPlurality: true, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Plurality = AttributePlurality.MultiValued });

        var dto = (result as OkObjectResult)?.Value as ConnectedSystemAttributeDto;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(attribute.PluralitySetByAdministrator, Is.True);
            Assert.That(dto?.PluralitySetByAdministrator, Is.True);
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_PluralityOnANonSupportingConnector_ReturnsBadRequestAsync()
    {
        Arrange(supportsPlurality: false, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Plurality = AttributePlurality.MultiValued });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_PluralityChangeOnAReferencedAttribute_ReturnsBadRequestAsync()
    {
        Arrange(supportsPlurality: true, out var attribute);
        _mockConnectedSystemRepo
            .Setup(r => r.IsObjectTypeAttributeBeingReferencedAsync(It.IsAny<ConnectedSystemObjectTypeAttribute>()))
            .ReturnsAsync(true);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Plurality = AttributePlurality.MultiValued });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_MultiValuedBoolean_ReturnsBadRequestAsync()
    {
        Arrange(supportsPlurality: true, out var attribute);
        attribute.Type = AttributeDataType.Boolean;

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Plurality = AttributePlurality.MultiValued });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_TypeBooleanWithMultiValuedSentTogether_ReturnsBadRequestAndChangesNothingAsync()
    {
        // The type checked is the one the attribute will have, so the two fields cannot disagree.
        Arrange(supportsPlurality: true, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Type = AttributeDataType.Boolean, Plurality = AttributePlurality.MultiValued });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.SingleValued));
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_NoPluralitySupplied_LeavesItAsDiscoveredAsync()
    {
        Arrange(supportsPlurality: false, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Selected = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(attribute.PluralitySetByAdministrator, Is.False);
        }
    }

    [Test]
    public async Task BulkUpdateConnectedSystemAttributesAsync_CarryingAPlurality_ReturnsBadRequestAsync()
    {
        // Refused rather than dropped, as a data type is: the bulk path carries only the selection flags.
        Arrange(supportsPlurality: true, out _);
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(ObjectTypeId)).ReturnsAsync(new ConnectedSystemObjectType
        {
            Id = ObjectTypeId,
            Name = "User",
            ConnectedSystemId = ConnectedSystemId
        });

        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(ConnectedSystemId, ObjectTypeId,
            new BulkUpdateConnectedSystemAttributesRequest
            {
                Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
                {
                    [AttributeId] = new() { Plurality = AttributePlurality.MultiValued }
                }
            });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    /// <summary>
    /// A delimited file's <c>member</c> column, discovered single-valued, on a Connected System whose Connector declares
    /// the capability under test.
    /// </summary>
    private void Arrange(bool supportsPlurality, out ConnectedSystemObjectTypeAttribute attribute)
    {
        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "HR File",
            ConnectorDefinition = new ConnectorDefinition
            {
                Id = 1,
                Name = "JIM File Connector",
                SupportsUserSelectedAttributeTypes = true,
                SupportsUserSelectedAttributePlurality = supportsPlurality
            }
        };

        attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = AttributeId,
            Name = "member",
            Type = AttributeDataType.Text,
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Id = ObjectTypeId,
                Name = "User",
                ConnectedSystemId = ConnectedSystemId
            }
        };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepo.Setup(r => r.GetAttributeAsync(AttributeId)).ReturnsAsync(attribute);
    }
}
