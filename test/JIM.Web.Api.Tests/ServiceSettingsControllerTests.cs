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

    #endregion
}
