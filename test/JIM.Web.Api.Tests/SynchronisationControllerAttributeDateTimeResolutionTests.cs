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
/// Covers an administrator overriding the step of time a Connected System holds a date attribute to (#2153, decision
/// D2), through the single and bulk attribute updates, and the resolution reaching the attribute DTO.
/// </summary>
/// <remarks>
/// Offered on every Connector, as the case-exact declaration is: discovery states a default (SCIM assumes whole seconds,
/// since a service provider's schema cannot say), and an administrator who knows better must be able to say so in a way
/// the next schema refresh leaves alone.
/// </remarks>
[TestFixture]
public class SynchronisationControllerAttributeDateTimeResolutionTests
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
    public async Task UpdateConnectedSystemAttributeAsync_ResolutionOnADateAttribute_IsAppliedAndRecordedAsChosenAsync()
    {
        Arrange(AttributeDataType.DateTime, out var attribute);
        attribute.DateTimeResolution = DateTimeResolution.Second;

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { DateTimeResolution = DateTimeResolution.Millisecond });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(attribute.DateTimeResolution, Is.EqualTo(DateTimeResolution.Millisecond));
            Assert.That(attribute.DateTimeResolutionSetByAdministrator, Is.True,
                "Recorded so the next schema refresh leaves the administrator's resolution alone.");
            var dto = (ConnectedSystemAttributeDto)((OkObjectResult)result).Value!;
            Assert.That(dto.DateTimeResolution, Is.EqualTo("Millisecond"));
            Assert.That(dto.DateTimeResolutionSetByAdministrator, Is.True);
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_ResolutionOmitted_LeavesItAloneAsync()
    {
        Arrange(AttributeDataType.DateTime, out var attribute);
        attribute.DateTimeResolution = DateTimeResolution.Second;

        await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { Selected = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.DateTimeResolution, Is.EqualTo(DateTimeResolution.Second));
            Assert.That(attribute.DateTimeResolutionSetByAdministrator, Is.False);
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_ResolutionOnANonDateAttribute_ReturnsBadRequestAsync()
    {
        // A resolution only means something for a date; accepting it elsewhere would store a setting nothing reads.
        Arrange(AttributeDataType.Text, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { DateTimeResolution = DateTimeResolution.Second });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(attribute.DateTimeResolution, Is.Null);
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAttributeAsync_UnknownResolution_ReturnsBadRequestAsync()
    {
        Arrange(AttributeDataType.DateTime, out var attribute);

        var result = await _controller.UpdateConnectedSystemAttributeAsync(ConnectedSystemId, ObjectTypeId, AttributeId,
            new UpdateConnectedSystemAttributeRequest { DateTimeResolution = (DateTimeResolution)42 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(attribute.DateTimeResolution, Is.Null);
        }
    }

    [Test]
    public async Task BulkUpdateConnectedSystemAttributesAsync_Resolution_IsAppliedToDatesAndRefusedForOthersAsync()
    {
        Arrange(AttributeDataType.DateTime, out var attribute);
        var text = new ConnectedSystemObjectTypeAttribute { Id = AttributeId + 1, Name = "DISPLAY_NAME", Type = AttributeDataType.Text };
        ArrangeObjectType(attribute, text);

        var result = await _controller.BulkUpdateConnectedSystemAttributesAsync(ConnectedSystemId, ObjectTypeId,
            new BulkUpdateConnectedSystemAttributesRequest
            {
                Attributes = new Dictionary<int, UpdateConnectedSystemAttributeRequest>
                {
                    [AttributeId] = new() { DateTimeResolution = DateTimeResolution.Second },
                    [text.Id] = new() { DateTimeResolution = DateTimeResolution.Second }
                }
            });

        var response = (BulkUpdateConnectedSystemAttributesResponse)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attribute.DateTimeResolution, Is.EqualTo(DateTimeResolution.Second));
            Assert.That(attribute.DateTimeResolutionSetByAdministrator, Is.True);
            Assert.That(text.DateTimeResolution, Is.Null, "A non-date attribute is refused, not silently given a resolution.");
            Assert.That(response.Errors, Has.Count.EqualTo(1));
            Assert.That(response.Errors![0].AttributeId, Is.EqualTo(text.Id));
        }
    }

    private void ArrangeObjectType(params ConnectedSystemObjectTypeAttribute[] attributes)
    {
        _mockConnectedSystemRepo.Setup(r => r.GetObjectTypeAsync(ObjectTypeId)).ReturnsAsync(new ConnectedSystemObjectType
        {
            Id = ObjectTypeId,
            Name = "Person",
            ConnectedSystemId = ConnectedSystemId,
            Attributes = [.. attributes]
        });
    }

    private void Arrange(AttributeDataType type, out ConnectedSystemObjectTypeAttribute attribute)
    {
        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "HR",
            ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "JIM File Connector" }
        };

        attribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = AttributeId,
            Name = "LOGIN",
            Type = type,
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
