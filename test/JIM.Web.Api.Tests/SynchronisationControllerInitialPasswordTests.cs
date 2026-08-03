// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
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
/// The REST surface for a Synchronisation Rule's initial password configuration (#1121).
/// <para>
/// A sub-resource rather than fields on the rule, so these tests also stand as the record of that choice: the
/// list projection cannot carry the configuration without reporting every rule as unconfigured.
/// </para>
/// </summary>
[TestFixture]
public class SynchronisationControllerInitialPasswordTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            _application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

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
    }

    /// <summary>
    /// A rule that has never had an initial password configured reports it switched off with JIM's defaults,
    /// rather than a 404 or a null. That is exactly how such a rule behaves, and a caller comparing settings
    /// across rules should not have to special-case the ones nobody has touched.
    /// </summary>
    [Test]
    public async Task GetSyncRuleInitialPasswordAsync_WithNothingConfigured_ReportsItSwitchedOffAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(1)).ReturnsAsync(BuildProvisioningRule(1));

        var result = await _controller.GetSyncRuleInitialPasswordAsync(1);

        var response = ((OkObjectResult)result).Value as SyncRuleInitialPasswordResponse;
        Assert.That(response, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(response!.Enabled, Is.False);
            Assert.That(response!.Source, Is.EqualTo(InitialPasswordSource.Discovered));
            Assert.That(response!.CustomPolicy.Length, Is.EqualTo(new PasswordGenerationPolicy().Length));
        });
    }

    [Test]
    public async Task GetSyncRuleInitialPasswordAsync_WithNoSuchRule_ReturnsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(99)).ReturnsAsync((SyncRule?)null);

        var result = await _controller.GetSyncRuleInitialPasswordAsync(99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    /// <summary>
    /// The whole configuration round-trips, and the response reports what was stored rather than what was asked
    /// for.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_PersistsEverySettingAsync()
    {
        var syncRule = BuildProvisioningRule(2);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(2)).ReturnsAsync(syncRule);

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(2, new UpdateSyncRuleInitialPasswordRequest
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires,
            EnableAccount = false,
            CustomPolicy = new PasswordGenerationPolicyDto
            {
                Style = PasswordGenerationStyle.Words,
                WordCount = 5,
                WordSeparator = PasswordWordSeparator.Underscore,
                WordCapitalisation = PasswordWordCapitalisation.EachWord,
                AppendedDigitCount = 2,
                AppendSymbol = true
            }
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var stored = syncRule.InitialPassword;
        Assert.That(stored, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Enabled, Is.True);
            Assert.That(stored!.Source, Is.EqualTo(InitialPasswordSource.Custom));
            Assert.That(stored!.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
            Assert.That(stored!.EnableAccount, Is.False);
            Assert.That(stored!.CustomPolicy.Style, Is.EqualTo(PasswordGenerationStyle.Words));
            Assert.That(stored!.CustomPolicy.WordCount, Is.EqualTo(5));
            Assert.That(stored!.CustomPolicy.AppendSymbol, Is.True);
        });
    }

    /// <summary>
    /// An omitted field leaves the stored value alone, matching the rule's own update endpoint.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_WithOmittedFields_LeavesThemUnchangedAsync()
    {
        var syncRule = BuildProvisioningRule(3);
        syncRule.InitialPassword = new SyncRuleInitialPassword
        {
            SyncRuleId = 3,
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires,
            EnableAccount = false
        };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(3)).ReturnsAsync(syncRule);

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(3, new UpdateSyncRuleInitialPasswordRequest
        {
            EnableAccount = true
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.Multiple(() =>
        {
            Assert.That(syncRule.InitialPassword!.EnableAccount, Is.True);
            Assert.That(syncRule.InitialPassword!.Source, Is.EqualTo(InitialPasswordSource.Custom), "an omitted field must not be reset to its default");
            Assert.That(syncRule.InitialPassword!.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.NeverExpires));
        });
    }

    /// <summary>
    /// A rule that does not provision is refused rather than quietly storing a setting that could never do
    /// anything. Only an account JIM has just created has never had a password.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_OnARuleThatDoesNotProvision_IsRefusedAsync()
    {
        var syncRule = BuildProvisioningRule(4);
        syncRule.ProvisionToConnectedSystem = false;
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(4)).ReturnsAsync(syncRule);

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(4, new UpdateSyncRuleInitialPasswordRequest { Enabled = true });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    /// <summary>
    /// Switching an initial password off on an Import rule is allowed, because it changes nothing; only turning
    /// one on needs a rule that provisions.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_SwitchingOffOnANonProvisioningRule_IsAllowedAsync()
    {
        var syncRule = BuildProvisioningRule(5);
        syncRule.ProvisionToConnectedSystem = false;
        syncRule.InitialPassword = new SyncRuleInitialPassword { SyncRuleId = 5, Enabled = true };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(5)).ReturnsAsync(syncRule);

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(5, new UpdateSyncRuleInitialPasswordRequest { Enabled = false });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.That(syncRule.InitialPassword!.Enabled, Is.False);
    }

    /// <summary>
    /// Settings that cannot produce a password are refused at the point of saving, not one account at a time
    /// afterwards. The administrator saving them is the person who can fix them.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_WithUnsatisfiableSettings_IsRefusedAsync()
    {
        var syncRule = BuildProvisioningRule(6);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(6)).ReturnsAsync(syncRule);

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(6, new UpdateSyncRuleInitialPasswordRequest
        {
            Enabled = true,
            Source = InitialPasswordSource.Custom,
            // Four characters cannot carry six required ones.
            CustomPolicy = new PasswordGenerationPolicyDto
            {
                Style = PasswordGenerationStyle.RandomCharacters,
                Length = 4,
                MinimumUppercase = 2,
                MinimumLowercase = 2,
                MinimumDigits = 1,
                MinimumSymbols = 1
            }
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    private static SyncRule BuildProvisioningRule(int id) =>
        new()
        {
            Id = id,
            Name = "Provision Users",
            Direction = SyncRuleDirection.Export,
            ProvisionToConnectedSystem = true,
            ConnectedSystemId = 100,
            ConnectedSystem = new ConnectedSystem { Id = 100, Name = "Yellowstone Directory", ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem },
            ConnectedSystemObjectTypeId = 200,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 200, Name = "user" },
            MetaverseObjectTypeId = 300,
            MetaverseObjectType = new MetaverseObjectType { Id = 300, Name = "User" },
            OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect
        };
}
