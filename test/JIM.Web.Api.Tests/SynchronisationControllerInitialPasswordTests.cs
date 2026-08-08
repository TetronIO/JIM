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
using JIM.Models.Transactional.DTOs;
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
    private Mock<ISyncRepository> _mockSyncRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private RoundTripCredentialProtection _credentialProtection = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();

        // Passed explicitly: JimApplication.SyncRepo comes from this constructor parameter, not from
        // IRepository.Sync, so omitting it leaves the initial-password server with a null repository and the
        // parked-work reporting on this endpoint throws. All three hosts pass it.
        _mockSyncRepo = new Mock<ISyncRepository>();
        _mockSyncRepo.Setup(r => r.GetParkedInitialPasswordReasonsAsync(It.IsAny<int>())).ReturnsAsync([]);
        _mockSyncRepo.Setup(r => r.GetInitialPasswordAttentionBySyncRuleAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync([]);

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockConnectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        // Set on the facade, not just handed to the controller: the static password is encrypted through the
        // application layer, and without this the fallback would build a real Data Protection provider and touch
        // the filesystem from a unit test.
        _credentialProtection = new RoundTripCredentialProtection();
        _application = new JimApplication(_mockRepository.Object, syncRepository: _mockSyncRepo.Object)
        {
            CredentialProtection = _credentialProtection
        };
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

    #region Parked work reporting (#1221)

    /// <summary>
    /// The parked work travels with the settings that caused it. An administrator scripting a check over every
    /// rule should get the answer from the response they were already fetching, not from a second call per rule.
    /// </summary>
    [Test]
    public async Task GetSyncRuleInitialPassword_WithParkedAccounts_ReportsThemWithTheSettingsAsync()
    {
        var rule = BuildProvisioningRule(5);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(5)).ReturnsAsync(rule);
        _mockSyncRepo.Setup(r => r.GetParkedInitialPasswordReasonsAsync(5)).ReturnsAsync([
            new InitialPasswordRejection
            {
                TargetMessage = "0000052D: CONSTRAINT_ATT_TYPE",
                FailureReason = PasswordSetFailureReason.PolicyRejection,
                AccountCount = 11,
                FirstSeenAt = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc)
            },
            new InitialPasswordRejection { TargetMessage = "Too short.", AccountCount = 3 }
        ]);
        _mockSyncRepo.Setup(r => r.GetInitialPasswordAttentionBySyncRuleAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new Dictionary<int, InitialPasswordAttention>
            {
                [5] = new InitialPasswordAttention { ParkedCount = 14, ExpiredCount = 2 }
            });

        var result = await _controller.GetSyncRuleInitialPasswordAsync(5);
        var response = (SyncRuleInitialPasswordResponse)((OkObjectResult)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(response.ParkedAccountCount, Is.EqualTo(14));
            Assert.That(response.ExpiredAccountCount, Is.EqualTo(2),
                "never folded into the parked count: correcting these settings does nothing for an expired account");
            Assert.That(response.ParkedReasons, Has.Count.EqualTo(2));
            Assert.That(response.ParkedReasons[0].TargetMessage, Is.EqualTo("0000052D: CONSTRAINT_ATT_TYPE"),
                "verbatim, because the code is what identifies the fault");
            Assert.That(response.ParkedReasons[0].AccountCount, Is.EqualTo(11));
            Assert.That(response.ParkedReasons[0].FailureReason, Is.EqualTo(PasswordSetFailureReason.PolicyRejection));
            Assert.That(response.ParkedReasons[0].FirstSeenAt, Is.EqualTo(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc)));
        });
    }

    [Test]
    public async Task GetSyncRuleInitialPassword_WithNothingParked_ReportsAnEmptyListAndZeroesAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(5)).ReturnsAsync(BuildProvisioningRule(5));

        var result = await _controller.GetSyncRuleInitialPasswordAsync(5);
        var response = (SyncRuleInitialPasswordResponse)((OkObjectResult)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(response.ParkedReasons, Is.Empty);
            Assert.That(response.ParkedAccountCount, Is.Zero);
            Assert.That(response.ExpiredAccountCount, Is.Zero);
        });
    }

    /// <summary>
    /// The response carries what a target said about a password and must never carry the password itself.
    /// </summary>
    [Test]
    public async Task GetSyncRuleInitialPassword_NeverReturnsAPasswordAlongsideTheReasonsAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(5)).ReturnsAsync(BuildProvisioningRule(5));
        _mockSyncRepo.Setup(r => r.GetParkedInitialPasswordReasonsAsync(5)).ReturnsAsync([
            new InitialPasswordRejection { TargetMessage = "Rejected.", AccountCount = 1 }
        ]);

        var result = await _controller.GetSyncRuleInitialPasswordAsync(5);
        var response = (SyncRuleInitialPasswordResponse)((OkObjectResult)result).Value!;

        Assert.That(typeof(SyncRuleInitialPasswordResponse).GetProperties(),
            Has.None.Matches<System.Reflection.PropertyInfo>(p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                                                                  && p.PropertyType == typeof(string)),
            "no string property on this response may be a password: a generated one is never stored, and the static " +
            "one (#1273) is stored but write-only on every surface");
        Assert.That(response.ParkedReasons, Has.Count.EqualTo(1));
    }

    #endregion

    #region The static password (#1273)

    /// <summary>
    /// The one password JIM stores goes in encrypted and never comes back out. A caller can learn that one is
    /// set and when it last changed, which is what a shared password's rotation actually needs.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_WithAStaticPassword_StoresItEncryptedAsync()
    {
        const string password = "Brown-Chicken-Ladder-47";
        var syncRule = BuildProvisioningRule(10);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(10)).ReturnsAsync(syncRule);

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(10, new UpdateSyncRuleInitialPasswordRequest
        {
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPassword = password
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var stored = syncRule.InitialPassword!.StaticPasswordEncryptedValue;
        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.Not.Null.And.Not.EqualTo(password), "the plaintext must never be what is stored");
            Assert.That(_credentialProtection.Unprotect(stored), Is.EqualTo(password), "and it must round-trip");
            Assert.That(syncRule.InitialPassword!.StaticPasswordSetAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_WithAStaticPassword_DoesNotReturnItAsync()
    {
        const string password = "Brown-Chicken-Ladder-47";
        var syncRule = BuildProvisioningRule(11);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(11)).ReturnsAsync(syncRule);

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(11, new UpdateSyncRuleInitialPasswordRequest
        {
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPassword = password
        });

        var json = System.Text.Json.JsonSerializer.Serialize(((OkObjectResult)result).Value);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain(password));
            Assert.That(json, Does.Not.Contain(syncRule.InitialPassword!.StaticPasswordEncryptedValue!),
                "nor the ciphertext, which is the password to anyone holding the encryption key");
        });
    }

    /// <summary>
    /// An omitted password means "leave the stored one alone", which is what makes the field write-only without a
    /// special case anywhere. Re-encrypting an unchanged password would also read as a change and release every
    /// account parked against this rule for nothing.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_WithoutAStaticPassword_LeavesTheStoredOneUnchangedAsync()
    {
        var syncRule = BuildProvisioningRule(12);
        var alreadyStored = _credentialProtection.Protect("Brown-Chicken-Ladder-47");
        var setAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        syncRule.InitialPassword = new SyncRuleInitialPassword
        {
            SyncRuleId = 12,
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPasswordEncryptedValue = alreadyStored,
            StaticPasswordSetAt = setAt
        };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(12)).ReturnsAsync(syncRule);

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(12, new UpdateSyncRuleInitialPasswordRequest
        {
            ExpiryBehaviour = PasswordExpiryBehaviour.NeverExpires
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        Assert.Multiple(() =>
        {
            Assert.That(syncRule.InitialPassword!.StaticPasswordEncryptedValue, Is.EqualTo(alreadyStored));
            Assert.That(syncRule.InitialPassword!.StaticPasswordSetAt, Is.EqualTo(setAt), "unchanged means unchanged, including when it changed");
        });
    }

    /// <summary>
    /// Refused at the point of saving for the same reason an unsatisfiable generator configuration is: one static
    /// password goes to every account this rule provisions, so a rejection is not one account's problem.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_WithAStaticPasswordTheTargetWouldRefuse_IsRefusedAsync()
    {
        var syncRule = BuildProvisioningRule(13);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(13)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(100))
            .ReturnsAsync(new ConnectedSystemPasswordPolicy { MinimumLength = 30 });

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(13, new UpdateSyncRuleInitialPasswordRequest
        {
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPassword = "short"
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_RefusingAStaticPassword_DoesNotRepeatItAsync()
    {
        const string password = "short";
        var syncRule = BuildProvisioningRule(14);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(14)).ReturnsAsync(syncRule);
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(100))
            .ReturnsAsync(new ConnectedSystemPasswordPolicy { MinimumLength = 30 });

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(14, new UpdateSyncRuleInitialPasswordRequest
        {
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPassword = password
        });

        var json = System.Text.Json.JsonSerializer.Serialize(((BadRequestObjectResult)result).Value);
        Assert.That(json, Does.Not.Contain(password), "an error body is logged and displayed like any other");
    }

    /// <summary>
    /// A rule that will use one static password but has none is refused rather than saved: delivery would park
    /// every account it provisions, and the administrator saving it is the person who can fix it.
    /// </summary>
    [Test]
    public async Task UpdateSyncRuleInitialPasswordAsync_SelectingTheStaticSourceWithNoPassword_IsRefusedAsync()
    {
        var syncRule = BuildProvisioningRule(15);
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(15)).ReturnsAsync(syncRule);

        var result = await _controller.UpdateSyncRuleInitialPasswordAsync(15, new UpdateSyncRuleInitialPasswordRequest
        {
            Enabled = true,
            Source = InitialPasswordSource.Static
        });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        _mockConnectedSystemRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public async Task GetSyncRuleInitialPasswordAsync_WithAStaticPassword_ReportsThatOneIsSetAndWhenAsync()
    {
        var setAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var syncRule = BuildProvisioningRule(16);
        syncRule.InitialPassword = new SyncRuleInitialPassword
        {
            SyncRuleId = 16,
            Enabled = true,
            Source = InitialPasswordSource.Static,
            StaticPasswordEncryptedValue = _credentialProtection.Protect("Brown-Chicken-Ladder-47"),
            StaticPasswordSetAt = setAt
        };
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(16)).ReturnsAsync(syncRule);

        var result = await _controller.GetSyncRuleInitialPasswordAsync(16);
        var response = (SyncRuleInitialPasswordResponse)((OkObjectResult)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(response.StaticPasswordSet, Is.True);
            Assert.That(response.StaticPasswordSetAt, Is.EqualTo(setAt));
        });
    }

    [Test]
    public async Task GetSyncRuleInitialPasswordAsync_WithNoStaticPassword_ReportsThatNoneIsSetAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetSyncRuleAsync(17)).ReturnsAsync(BuildProvisioningRule(17));

        var result = await _controller.GetSyncRuleInitialPasswordAsync(17);
        var response = (SyncRuleInitialPasswordResponse)((OkObjectResult)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(response.StaticPasswordSet, Is.False);
            Assert.That(response.StaticPasswordSetAt, Is.Null);
        });
    }

    #endregion

    /// <summary>
    /// Credential protection that round-trips through a recognisable prefix and base64, so the ciphertext never
    /// literally contains the plaintext and a test can assert that neither reached a response.
    /// </summary>
    private sealed class RoundTripCredentialProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) || IsProtected(plainText)
                ? plainText
                : Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

        public string? Unprotect(string? protectedData) =>
            IsProtected(protectedData)
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedData![Prefix.Length..]))
                : protectedData;
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
