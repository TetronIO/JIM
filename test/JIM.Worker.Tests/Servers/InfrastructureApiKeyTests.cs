// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests <see cref="SecurityServer.EnsureInfrastructureApiKeyAsync"/>, which JIM.Web calls at startup with the hash of
/// the JIM_INFRASTRUCTURE_API_KEY environment variable. The key must be created when absent and renewed when it has
/// expired, so that restarting JIM always restores a working recovery key (for example after a Factory Reset that
/// removed every administrator); a key an administrator disabled must stay disabled.
/// </summary>
[TestFixture]
public class InfrastructureApiKeyTests
{
    private const string KeyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string KeyPrefix = "jim_ak_infra";

    private Mock<IRepository> _repo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<ISecurityRepository> _securityRepo = null!;
    private JimApplication _jim = null!;
    private Role _adminRole = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _securityRepo = new Mock<ISecurityRepository>();
        var activityRepo = new Mock<IActivityRepository>();
        var settingsRepo = new Mock<IServiceSettingsRepository>();
        _repo.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);
        _repo.Setup(r => r.Security).Returns(_securityRepo.Object);
        _repo.Setup(r => r.Activity).Returns(activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(settingsRepo.Object);

        activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>())).ReturnsAsync((ApiKey k) => k);
        _apiKeyRepo.Setup(r => r.UpdateAsync(It.IsAny<ApiKey>())).ReturnsAsync((ApiKey k) => k);

        _adminRole = new Role { Id = 1, Name = "Administrator" };
        _securityRepo.Setup(r => r.GetRoleAsync("Administrator")).ReturnsAsync(_adminRole);

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task EnsureInfrastructureApiKeyAsync_NoExistingKey_CreatesAdministratorKeyExpiringIn24HoursAsync()
    {
        ApiKey? created = null;
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>())).Callback<ApiKey>(k => created = k).ReturnsAsync((ApiKey k) => k);

        var outcome = await _jim.Security.EnsureInfrastructureApiKeyAsync(KeyHash, KeyPrefix);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome, Is.EqualTo(InfrastructureApiKeyOutcome.Created));
            Assert.That(created, Is.Not.Null);
            Assert.That(created!.KeyHash, Is.EqualTo(KeyHash));
            Assert.That(created.KeyPrefix, Is.EqualTo(KeyPrefix));
            Assert.That(created.IsInfrastructureKey, Is.True);
            Assert.That(created.IsEnabled, Is.True);
            Assert.That(created.Roles, Is.EquivalentTo(new[] { _adminRole }));
            Assert.That(created.ExpiresAt, Is.EqualTo(DateTime.UtcNow.AddHours(24)).Within(TimeSpan.FromMinutes(1)));
        }
    }

    [Test]
    public async Task EnsureInfrastructureApiKeyAsync_ExpiredKey_RenewsExpiryFor24HoursAsync()
    {
        var existing = NewExistingKey(expiresAt: DateTime.UtcNow.AddDays(-3));
        _apiKeyRepo.Setup(r => r.GetByHashAsync(KeyHash)).ReturnsAsync(existing);

        var outcome = await _jim.Security.EnsureInfrastructureApiKeyAsync(KeyHash, KeyPrefix);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome, Is.EqualTo(InfrastructureApiKeyOutcome.Renewed));
            Assert.That(existing.ExpiresAt, Is.EqualTo(DateTime.UtcNow.AddHours(24)).Within(TimeSpan.FromMinutes(1)));
        }
        _apiKeyRepo.Verify(r => r.UpdateAsync(existing), Times.Once);
        _apiKeyRepo.Verify(r => r.CreateAsync(It.IsAny<ApiKey>()), Times.Never);
    }

    [Test]
    public async Task EnsureInfrastructureApiKeyAsync_UnexpiredKey_LeavesItUnchangedAsync()
    {
        var expiresAt = DateTime.UtcNow.AddHours(5);
        var existing = NewExistingKey(expiresAt);
        _apiKeyRepo.Setup(r => r.GetByHashAsync(KeyHash)).ReturnsAsync(existing);

        var outcome = await _jim.Security.EnsureInfrastructureApiKeyAsync(KeyHash, KeyPrefix);

        Assert.That(outcome, Is.EqualTo(InfrastructureApiKeyOutcome.Unchanged));
        Assert.That(existing.ExpiresAt, Is.EqualTo(expiresAt));
        _apiKeyRepo.Verify(r => r.UpdateAsync(It.IsAny<ApiKey>()), Times.Never);
    }

    [Test]
    public async Task EnsureInfrastructureApiKeyAsync_DisabledExpiredKey_LeavesItDisabledAndExpiredAsync()
    {
        var expiresAt = DateTime.UtcNow.AddDays(-1);
        var existing = NewExistingKey(expiresAt);
        existing.IsEnabled = false;
        _apiKeyRepo.Setup(r => r.GetByHashAsync(KeyHash)).ReturnsAsync(existing);

        var outcome = await _jim.Security.EnsureInfrastructureApiKeyAsync(KeyHash, KeyPrefix);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome, Is.EqualTo(InfrastructureApiKeyOutcome.Disabled));
            Assert.That(existing.IsEnabled, Is.False);
            Assert.That(existing.ExpiresAt, Is.EqualTo(expiresAt));
        }
        _apiKeyRepo.Verify(r => r.UpdateAsync(It.IsAny<ApiKey>()), Times.Never);
    }

    [Test]
    public async Task EnsureInfrastructureApiKeyAsync_NoAdministratorRole_CreatesNothingAsync()
    {
        _securityRepo.Setup(r => r.GetRoleAsync("Administrator")).ReturnsAsync((Role?)null);

        var outcome = await _jim.Security.EnsureInfrastructureApiKeyAsync(KeyHash, KeyPrefix);

        Assert.That(outcome, Is.EqualTo(InfrastructureApiKeyOutcome.AdministratorRoleMissing));
        _apiKeyRepo.Verify(r => r.CreateAsync(It.IsAny<ApiKey>()), Times.Never);
    }

    private static ApiKey NewExistingKey(DateTime expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Infrastructure Key",
        KeyHash = KeyHash,
        KeyPrefix = KeyPrefix,
        ExpiresAt = expiresAt,
        IsEnabled = true,
        IsInfrastructureKey = true
    };
}
