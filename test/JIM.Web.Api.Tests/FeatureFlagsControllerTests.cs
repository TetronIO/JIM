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

/// <summary>
/// Tests <see cref="FeatureFlagsController"/> (#1781): the includeInDevelopment default and pass-through, updating
/// a flag (including the In Development acknowledgement gate), and the not-found/bad-request status mapping.
/// </summary>
[TestFixture]
public class FeatureFlagsControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<ILogger<FeatureFlagsController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private FeatureFlagsController _controller = null!;
    private readonly Dictionary<string, ServiceSetting> _persisted = new();
    private Guid _apiKeyId;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockLogger = new Mock<ILogger<FeatureFlagsController>>();

        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);

        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ServiceSetting, It.IsAny<string>()))
            .ReturnsAsync(0);

        // Tracking disabled: these tests are about the feature-flag surface's status mapping, not configuration
        // change capture (covered elsewhere), so it is kept out of the way.
        _persisted[Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled] = new ServiceSetting
        {
            Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
            ValueType = ServiceSettingValueType.Boolean,
            Value = "false"
        };
        _persisted[FeatureFlagCatalogue.UniqueValueGeneration.Key] = new ServiceSetting
        {
            Key = FeatureFlagCatalogue.UniqueValueGeneration.Key,
            DisplayName = FeatureFlagCatalogue.UniqueValueGeneration.DisplayName,
            Category = ServiceSettingCategory.FeatureFlags,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "false",
            Value = "false"
        };

        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .Returns((string key) => Task.FromResult(_persisted.GetValueOrDefault(key)));
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Callback<ServiceSetting>(s => _persisted[s.Key] = s)
            .Returns(Task.CompletedTask);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new FeatureFlagsController(_mockLogger.Object, _application);

        // API-key authentication, matching ServiceSettingsControllerTests: FeatureFlagsController.UpdateAsync
        // resolves an interactive (JWT) caller to (MetaverseObject?)null, which ActivityServer.CreateActivityAsync
        // then rejects (InitiatedByType stays NotSet) - the same pre-existing gap ServiceSettingsController has.
        // Exercising that path is not this test's job; it authenticates as an API key so the write actually succeeds.
        _apiKeyId = Guid.NewGuid();
        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, _apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

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
    public void TearDown() => _application?.Dispose();

    #region GetAllAsync

    [Test]
    public async Task GetAllAsync_Default_OmitsInDevelopmentFlagsAsync()
    {
        var result = await _controller.GetAllAsync() as OkObjectResult;
        var flags = (result?.Value as IEnumerable<FeatureFlagDto>)?.ToList();

        Assert.That(flags, Is.Not.Null);
        Assert.That(flags!.Select(f => f.Key), Does.Not.Contain(FeatureFlagCatalogue.UniqueValueGeneration.Key));
    }

    [Test]
    public async Task GetAllAsync_IncludeInDevelopment_ReturnsEveryFlagAsync()
    {
        var result = await _controller.GetAllAsync(includeInDevelopment: true) as OkObjectResult;
        var flags = (result?.Value as IEnumerable<FeatureFlagDto>)?.ToList();

        Assert.That(flags, Is.Not.Null);
        Assert.That(flags!.Select(f => f.Key), Does.Contain(FeatureFlagCatalogue.UniqueValueGeneration.Key));
        var flag = flags!.Single(f => f.Key == FeatureFlagCatalogue.UniqueValueGeneration.Key);
        Assert.That(flag.Tier, Is.EqualTo(nameof(FeatureFlagTier.InDevelopment)));
        Assert.That(flag.TrackingIssueNumber, Is.EqualTo(242));
    }

    #endregion

    #region UpdateAsync

    [Test]
    public async Task UpdateAsync_Disable_ReturnsOkWithNewStateNoAcknowledgementNeededAsync()
    {
        // Disabling never needs allowInDevelopment, whatever the flag's tier; the seeded value starts true so the
        // change is real, not a no-op.
        var key = FeatureFlagCatalogue.UniqueValueGeneration.Key;
        _persisted[key].Value = "true";

        var result = await _controller.UpdateAsync(key, new FeatureFlagUpdateRequestDto { Enabled = false }) as OkObjectResult;
        var dto = result?.Value as FeatureFlagDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Enabled, Is.False);
        Assert.That(_persisted[key].Value, Is.EqualTo("false"));
    }

    [Test]
    public async Task UpdateAsync_UnknownKey_ReturnsNotFoundAsync()
    {
        var result = await _controller.UpdateAsync("Features.DoesNotExist", new FeatureFlagUpdateRequestDto { Enabled = true });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_EnableInDevelopmentFlagWithoutAcknowledgement_ReturnsBadRequestAsync()
    {
        var result = await _controller.UpdateAsync(FeatureFlagCatalogue.UniqueValueGeneration.Key,
            new FeatureFlagUpdateRequestDto { Enabled = true, AllowInDevelopment = false });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(_persisted[FeatureFlagCatalogue.UniqueValueGeneration.Key].Value, Is.EqualTo("false"));
    }

    [Test]
    public async Task UpdateAsync_EnableInDevelopmentFlagWithAcknowledgement_ReturnsOkAsync()
    {
        var result = await _controller.UpdateAsync(FeatureFlagCatalogue.UniqueValueGeneration.Key,
            new FeatureFlagUpdateRequestDto { Enabled = true, AllowInDevelopment = true }) as OkObjectResult;
        var dto = result?.Value as FeatureFlagDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Enabled, Is.True);
    }

    #endregion
}
