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
using JIM.Models.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class ServiceSettingsControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<ILogger<ServiceSettingsController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private ServiceSettingsController _controller = null!;
    private Guid _apiKeyId;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockLogger = new Mock<ILogger<ServiceSettingsController>>();

        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new ServiceSettingsController(_mockLogger.Object, _application);

        // Set up API key authentication context
        _apiKeyId = Guid.NewGuid();
        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, _apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Mock the API key lookup
        var apiKey = new ApiKey
        {
            Id = _apiKeyId,
            Name = "TestApiKey",
            KeyPrefix = "jim_ak_test",
            KeyHash = "hash",
            Roles = new List<Role>()
        };
        _mockApiKeyRepo.Setup(r => r.GetByIdAsync(_apiKeyId)).ReturnsAsync(apiKey);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    /// <summary>
    /// Re-authenticates the controller as a signed-in (JWT) user, shaped as the bearer pipeline leaves it: the token's
    /// identity plus JIM's own identity carrying the resolved Metaverse Object id, which GetCurrentUserAsync reads.
    /// </summary>
    private MetaverseObject AuthenticateAsInteractiveUser()
    {
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        var user = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "User" }, CachedDisplayName = "Admin User" };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectAsync(user.Id)).ReturnsAsync(user);
        var tokenIdentity = new ClaimsIdentity(new List<Claim> { new("sub", "idp-subject"), new(ClaimTypes.Name, "Admin User") }, "TestAuth");
        var jimIdentity = new ClaimsIdentity(new List<Claim> { new(Constants.BuiltInClaims.MetaverseObjectId, user.Id.ToString()) });
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new[] { tokenIdentity, jimIdentity }) } };
        return user;
    }

    #region GetAllAsync tests

    [Test]
    public async Task GetAllAsync_ReturnsOkResultAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetAllSettingsAsync())
            .ReturnsAsync(new List<ServiceSetting>());

        var result = await _controller.GetAllAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetAllAsync_ReturnsEmptyListWhenNoSettingsAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetAllSettingsAsync())
            .ReturnsAsync(new List<ServiceSetting>());

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var settings = result?.Value as IEnumerable<ServiceSettingDto>;

        Assert.That(settings, Is.Not.Null);
        Assert.That(settings!.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllSettingsAsync()
    {
        var settingsList = new List<ServiceSetting>
        {
            new() { Key = "Test.Setting1", DisplayName = "Test Setting 1", Category = ServiceSettingCategory.Synchronisation, ValueType = ServiceSettingValueType.Boolean, DefaultValue = "true" },
            new() { Key = "Test.Setting2", DisplayName = "Test Setting 2", Category = ServiceSettingCategory.Maintenance, ValueType = ServiceSettingValueType.Integer, DefaultValue = "100" }
        };
        _mockServiceSettingsRepo.Setup(r => r.GetAllSettingsAsync())
            .ReturnsAsync(settingsList);

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var settings = result?.Value as IEnumerable<ServiceSettingDto>;

        Assert.That(settings, Is.Not.Null);
        Assert.That(settings!.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetAllAsync_MapsEntityFieldsCorrectlyAsync()
    {
        var entity = new ServiceSetting
        {
            Key = "ChangeTracking.CsoChanges.Enabled",
            DisplayName = "CSO Change Tracking",
            Description = "Controls whether CSO changes are recorded",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            Value = "false",
            IsReadOnly = false
        };
        _mockServiceSettingsRepo.Setup(r => r.GetAllSettingsAsync())
            .ReturnsAsync(new List<ServiceSetting> { entity });

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var settings = (result?.Value as IEnumerable<ServiceSettingDto>)?.ToList();

        Assert.That(settings, Is.Not.Null);
        var dto = settings!.First();
        Assert.That(dto.Key, Is.EqualTo("ChangeTracking.CsoChanges.Enabled"));
        Assert.That(dto.DisplayName, Is.EqualTo("CSO Change Tracking"));
        Assert.That(dto.Description, Is.EqualTo("Controls whether CSO changes are recorded"));
        Assert.That(dto.Category, Is.EqualTo("Synchronisation"));
        Assert.That(dto.ValueType, Is.EqualTo("Boolean"));
        Assert.That(dto.DefaultValue, Is.EqualTo("true"));
        Assert.That(dto.Value, Is.EqualTo("false"));
        Assert.That(dto.EffectiveValue, Is.EqualTo("false"));
        Assert.That(dto.IsReadOnly, Is.False);
        Assert.That(dto.IsOverridden, Is.True);
    }

    #endregion

    #region GetByKeyAsync tests

    [Test]
    public async Task GetByKeyAsync_WithValidKey_ReturnsOkResultAsync()
    {
        var setting = new ServiceSetting
        {
            Key = "Test.Setting",
            DisplayName = "Test",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true"
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Test.Setting"))
            .ReturnsAsync(setting);

        var result = await _controller.GetByKeyAsync("Test.Setting");

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetByKeyAsync_WithInvalidKey_ReturnsNotFoundAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Nonexistent.Key"))
            .ReturnsAsync((ServiceSetting?)null);

        var result = await _controller.GetByKeyAsync("Nonexistent.Key");

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region UpdateAsync tests

    [Test]
    public async Task UpdateAsync_WithValidKey_ReturnsOkResultAsync()
    {
        var setting = new ServiceSetting
        {
            Key = "Test.Setting",
            DisplayName = "Test",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            IsReadOnly = false
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Test.Setting"))
            .ReturnsAsync(setting);
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Returns(Task.CompletedTask);

        var request = new ServiceSettingUpdateRequestDto { Value = "false" };
        var result = await _controller.UpdateAsync("Test.Setting", request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_WithInvalidKey_ReturnsNotFoundAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Nonexistent.Key"))
            .ReturnsAsync((ServiceSetting?)null);

        var request = new ServiceSettingUpdateRequestDto { Value = "false" };
        var result = await _controller.UpdateAsync("Nonexistent.Key", request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_WithReadOnlySetting_ReturnsBadRequestAsync()
    {
        var setting = new ServiceSetting
        {
            Key = "SSO.Authority",
            DisplayName = "SSO Authority",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.String,
            DefaultValue = "https://example.com",
            IsReadOnly = true
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("SSO.Authority"))
            .ReturnsAsync(setting);

        var request = new ServiceSettingUpdateRequestDto { Value = "https://other.com" };
        var result = await _controller.UpdateAsync("SSO.Authority", request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_ReturnsUpdatedSettingAsync()
    {
        var setting = new ServiceSetting
        {
            Key = "Test.Setting",
            DisplayName = "Test",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            IsReadOnly = false
        };

        // First two calls (controller check + ServiceSettingsServer internal) return original,
        // third call (re-fetch after update) returns updated
        var callCount = 0;
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Test.Setting"))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount <= 2) return setting;
                return new ServiceSetting
                {
                    Key = "Test.Setting",
                    DisplayName = "Test",
                    Category = ServiceSettingCategory.Synchronisation,
                    ValueType = ServiceSettingValueType.Boolean,
                    DefaultValue = "true",
                    Value = "false",
                    IsReadOnly = false
                };
            });
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Returns(Task.CompletedTask);

        var request = new ServiceSettingUpdateRequestDto { Value = "false" };
        var result = await _controller.UpdateAsync("Test.Setting", request) as OkObjectResult;
        var dto = result?.Value as ServiceSettingDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Value, Is.EqualTo("false"));
        Assert.That(dto.IsOverridden, Is.True);
    }

    [Test]
    public async Task UpdateAsync_InteractiveUser_RecordsTheUserAndReturnsOkAsync()
    {
        // A signed-in administrator calling the REST API (JWT, not an API key) is resolved to their Metaverse Object,
        // so the change is attributed to them rather than refused for having no initiator.
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Test.Setting")).ReturnsAsync(new ServiceSetting
        {
            Key = "Test.Setting",
            DisplayName = "Test",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            IsReadOnly = false
        });
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>())).Returns(Task.CompletedTask);
        var user = AuthenticateAsInteractiveUser();
        Activity? recorded = null;
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Callback<Activity>(a => recorded = a).Returns(Task.CompletedTask);

        var result = await _controller.UpdateAsync("Test.Setting", new ServiceSettingUpdateRequestDto { Value = "false" });

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), () => System.Text.Json.JsonSerializer.Serialize((result as ObjectResult)?.Value));
        Assert.That(recorded?.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(recorded?.InitiatedById, Is.EqualTo(user.Id));
    }

    #endregion

    #region RevertAsync tests

    [Test]
    public async Task RevertAsync_WithValidKey_ReturnsOkResultAsync()
    {
        var setting = new ServiceSetting
        {
            Key = "Test.Setting",
            DisplayName = "Test",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            Value = "false",
            IsReadOnly = false
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Test.Setting"))
            .ReturnsAsync(setting);
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.RevertAsync("Test.Setting");

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task RevertAsync_WithInvalidKey_ReturnsNotFoundAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Nonexistent.Key"))
            .ReturnsAsync((ServiceSetting?)null);

        var result = await _controller.RevertAsync("Nonexistent.Key");

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task RevertAsync_WithReadOnlySetting_ReturnsBadRequestAsync()
    {
        var setting = new ServiceSetting
        {
            Key = "SSO.Authority",
            DisplayName = "SSO Authority",
            Category = ServiceSettingCategory.SSO,
            ValueType = ServiceSettingValueType.String,
            IsReadOnly = true
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("SSO.Authority"))
            .ReturnsAsync(setting);

        var result = await _controller.RevertAsync("SSO.Authority");

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task RevertAsync_InteractiveUser_RecordsTheUserAndReturnsOkAsync()
    {
        // A signed-in administrator calling the REST API (JWT, not an API key) is resolved to their Metaverse Object,
        // so the revert is attributed to them rather than refused for having no initiator.
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Test.Setting")).ReturnsAsync(new ServiceSetting
        {
            Key = "Test.Setting",
            DisplayName = "Test",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            Value = "false",
            IsReadOnly = false
        });
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>())).Returns(Task.CompletedTask);
        var user = AuthenticateAsInteractiveUser();
        Activity? recorded = null;
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Callback<Activity>(a => recorded = a).Returns(Task.CompletedTask);

        var result = await _controller.RevertAsync("Test.Setting");

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), () => System.Text.Json.JsonSerializer.Serialize((result as ObjectResult)?.Value));
        Assert.That(recorded?.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(recorded?.InitiatedById, Is.EqualTo(user.Id));
    }

    [Test]
    public async Task UpdateAsync_InteractiveUser_ResolvesTheUserFromTheMetaverseObjectIdClaimAloneAsync()
    {
        // The bearer pipeline has already resolved the caller and attached their Metaverse Object id; the controller
        // must attribute the change to that id, not resolve the caller a second time through the SSO attribute lookup
        // (which could disagree with the identity authorisation was granted to).
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Test.Setting")).ReturnsAsync(new ServiceSetting
        {
            Key = "Test.Setting",
            DisplayName = "Test",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            IsReadOnly = false
        });
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>())).Returns(Task.CompletedTask);
        var user = AuthenticateAsInteractiveUser();

        // Wire the SSO attribute lookup fully, resolving to a different Metaverse Object, so a second resolution
        // would both be reachable and visibly misattribute the change.
        var ssoAttribute = new MetaverseAttribute { Id = 1, Name = "SsoId" };
        _mockServiceSettingsRepo.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings
        {
            SSOUniqueIdentifierClaimType = "sub",
            SSOUniqueIdentifierMetaverseAttribute = ssoAttribute
        });
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(It.IsAny<string>(), false, It.IsAny<bool>())).ReturnsAsync(user.Type);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(It.IsAny<MetaverseObjectType>(), It.IsAny<MetaverseAttribute>(), It.IsAny<string>()))
            .ReturnsAsync(new MetaverseObject { Id = Guid.NewGuid(), Type = user.Type, CachedDisplayName = "Someone Else" });
        Activity? recorded = null;
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Callback<Activity>(a => recorded = a).Returns(Task.CompletedTask);

        await _controller.UpdateAsync("Test.Setting", new ServiceSettingUpdateRequestDto { Value = "false" });

        Assert.That(recorded?.InitiatedById, Is.EqualTo(user.Id));
        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectByTypeAndAttributeAsync(
            It.IsAny<MetaverseObjectType>(), It.IsAny<MetaverseAttribute>(), It.IsAny<string>()), Times.Never);
        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectTypeAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion

    #region Instance settings tests (#583)

    [Test]
    public async Task UpdateAsync_ServiceName_UpdatesSuccessfullyAsync()
    {
        var setting = new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceName,
            DisplayName = "Service Name",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.String,
            Value = null,
            IsReadOnly = false
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ServiceName))
            .ReturnsAsync(setting);
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Returns(Task.CompletedTask);

        var request = new ServiceSettingUpdateRequestDto { Value = "HQ-Production" };
        var result = await _controller.UpdateAsync(Constants.SettingKeys.ServiceName, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        _mockServiceSettingsRepo.Verify(r => r.UpdateSettingAsync(
            It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceName && s.Value == "HQ-Production")),
            Times.Once);
    }

    [Test]
    public async Task UpdateAsync_ServiceId_ReturnsBadRequestAsync()
    {
        var setting = new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceId,
            DisplayName = "Service ID",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.Guid,
            Value = Guid.NewGuid().ToString(),
            IsReadOnly = true
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ServiceId))
            .ReturnsAsync(setting);

        var request = new ServiceSettingUpdateRequestDto { Value = Guid.NewGuid().ToString() };
        var result = await _controller.UpdateAsync(Constants.SettingKeys.ServiceId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var body = ((BadRequestObjectResult)result).Value as ApiErrorResponse;
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Message, Does.Contain("read-only"));

        _mockServiceSettingsRepo.Verify(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()),
            Times.Never);
    }

    [Test]
    public async Task RevertAsync_ServiceId_ReturnsBadRequestAsync()
    {
        var setting = new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceId,
            DisplayName = "Service ID",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.Guid,
            Value = Guid.NewGuid().ToString(),
            IsReadOnly = true
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ServiceId))
            .ReturnsAsync(setting);

        var result = await _controller.RevertAsync(Constants.SettingKeys.ServiceId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockServiceSettingsRepo.Verify(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()),
            Times.Never);
    }

    [Test]
    public async Task GetByKeyAsync_ServiceId_ReturnsGuidStringAsync()
    {
        var id = Guid.NewGuid();
        var setting = new ServiceSetting
        {
            Key = Constants.SettingKeys.ServiceId,
            DisplayName = "Service ID",
            Category = ServiceSettingCategory.Instance,
            ValueType = ServiceSettingValueType.Guid,
            Value = id.ToString(),
            IsReadOnly = true
        };
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ServiceId))
            .ReturnsAsync(setting);

        var result = await _controller.GetByKeyAsync(Constants.SettingKeys.ServiceId) as OkObjectResult;
        var dto = result?.Value as ServiceSettingDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.ValueType, Is.EqualTo("Guid"));
        Assert.That(dto!.EffectiveValue, Is.EqualTo(id.ToString()));
        Assert.That(dto!.IsReadOnly, Is.True);
    }

    #endregion

    #region Feature flag exclusion (#1781)

    [Test]
    public async Task GetAllAsync_ExcludesFeatureFlagSettingsAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetAllSettingsAsync()).ReturnsAsync(new List<ServiceSetting>
        {
            new() { Key = "Test.Setting", DisplayName = "Test", Category = ServiceSettingCategory.Synchronisation, ValueType = ServiceSettingValueType.Boolean },
            new() { Key = "Features.SomeFlag", DisplayName = "Some Flag", Category = ServiceSettingCategory.FeatureFlags, ValueType = ServiceSettingValueType.Boolean }
        });

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var settings = (result?.Value as IEnumerable<ServiceSettingDto>)?.ToList();

        Assert.That(settings, Is.Not.Null);
        Assert.That(settings!.Select(s => s.Key), Does.Not.Contain("Features.SomeFlag"));
        Assert.That(settings!.Select(s => s.Key), Does.Contain("Test.Setting"));
    }

    [Test]
    public async Task GetByKeyAsync_FeatureFlagKey_RefusesWithBadRequestAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Features.SomeFlag")).ReturnsAsync(new ServiceSetting
        {
            Key = "Features.SomeFlag", DisplayName = "Some Flag", Category = ServiceSettingCategory.FeatureFlags, ValueType = ServiceSettingValueType.Boolean
        });

        var result = await _controller.GetByKeyAsync("Features.SomeFlag");

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_FeatureFlagKey_RefusesWithBadRequestAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Features.SomeFlag")).ReturnsAsync(new ServiceSetting
        {
            Key = "Features.SomeFlag", DisplayName = "Some Flag", Category = ServiceSettingCategory.FeatureFlags, ValueType = ServiceSettingValueType.Boolean
        });

        var result = await _controller.UpdateAsync("Features.SomeFlag", new ServiceSettingUpdateRequestDto { Value = "true" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task RevertAsync_FeatureFlagKey_RefusesWithBadRequestAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync("Features.SomeFlag")).ReturnsAsync(new ServiceSetting
        {
            Key = "Features.SomeFlag", DisplayName = "Some Flag", Category = ServiceSettingCategory.FeatureFlags, ValueType = ServiceSettingValueType.Boolean
        });

        var result = await _controller.RevertAsync("Features.SomeFlag");

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    #endregion
}
