// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Scheduling;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that a factory reset restores the provenance of the built-in API rate limiting Service Settings
/// (issue #500). Service Setting rows themselves are never truncated by <c>SystemRepository.ResetSystemAsync</c>
/// (they are not customer data), but the Activities table is, which loses the Create Activity and version-1
/// configuration snapshot that show they were created by JIM rather than an administrator. Without
/// <see cref="JIM.Application.Servers.SeedingServer.RebaselineBuiltInConfigurationAsync"/>, that provenance
/// would be permanently lost after a reset. Mirrors <c>SystemResetBuiltInScheduleTests</c>'s mock pattern,
/// extended with the repositories <c>RebaselineBuiltInConfigurationAsync</c> also reads.
/// </summary>
[TestFixture]
public class SystemResetServiceSettingBaselineTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<ISchedulingRepository> _schedulingRepo = null!;
    private Mock<ISystemRepository> _systemRepo = null!;
    private Mock<IExampleDataRepository> _exampleDataRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<ISearchRepository> _searchRepo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<ISecurityRepository> _securityRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private List<Activity> _createdActivities = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        // NUnit reuses a single fixture instance across every [Test] method by default, so this must be
        // (re)assigned here rather than via a field initialiser, or activities from one test would still be
        // present when the next test's CreateActivityAsync callback starts appending to it.
        _createdActivities = new List<Activity>();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _schedulingRepo = new Mock<ISchedulingRepository>();
        _systemRepo = new Mock<ISystemRepository>();
        _exampleDataRepo = new Mock<IExampleDataRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _searchRepo = new Mock<ISearchRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _securityRepo = new Mock<ISecurityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Scheduling).Returns(_schedulingRepo.Object);
        _repo.Setup(r => r.System).Returns(_systemRepo.Object);
        _repo.Setup(r => r.ExampleData).Returns(_exampleDataRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repo.Setup(r => r.Search).Returns(_searchRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Security).Returns(_securityRepo.Object);

        // No activities in progress, so the reset's integrity guard passes.
        _activityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
                It.IsAny<IEnumerable<ActivityOutcomeType>?>(), It.IsAny<IEnumerable<ActivityTargetType>?>(),
                It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(new PagedResultSet<Activity> { Results = new List<Activity>(), TotalResults = 0, PageSize = 1, CurrentPage = 1 });
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ServiceSetting, It.IsAny<string>())).ReturnsAsync(0);
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(It.IsAny<ActivityTargetType>(), It.IsAny<string>())).ReturnsAsync((string?)null);

        _systemRepo.Setup(r => r.ResetSystemAsync(It.IsAny<bool>())).ReturnsAsync(new SystemResetResult());

        // The built-in example data template is intact, so EnsureBuiltInExampleDataTemplateAsync's repair path is a no-op.
        var intactTemplate = new ExampleDataTemplate { Name = "Users & Groups", BuiltIn = true };
        var objectType = new ExampleDataObjectType { MetaverseObjectType = new MetaverseObjectType { Name = "User" } };
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute());
        intactTemplate.ObjectTypes.Add(objectType);
        _exampleDataRepo.Setup(r => r.GetTemplateAsync("Users & Groups")).ReturnsAsync(intactTemplate);

        // The built-in schedule already exists post-wipe (not the focus of this test), so SeedBuiltInSchedulesAsync no-ops.
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule>
        {
            new()
            {
                BuiltIn = true,
                Steps = new List<ScheduleStep> { new() { StepType = ScheduleStepType.TemporalScopeReconciliation } }
            }
        });

        // Empty for every other rebaselined configuration type: this test's focus is Service Settings.
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypesAsync(It.IsAny<bool>())).ReturnsAsync(new List<MetaverseObjectType>());
        _metaverseRepo.Setup(r => r.GetMetaverseAttributesAsync()).ReturnsAsync(new List<MetaverseAttribute>());
        _searchRepo.Setup(r => r.GetPredefinedSearchHeadersAsync()).ReturnsAsync(new List<JIM.Models.Search.DTOs.PredefinedSearchHeader>());
        _connectedSystemRepo.Setup(r => r.GetConnectorDefinitionHeadersAsync()).ReturnsAsync(new List<JIM.Models.Staging.DTOs.ConnectorDefinitionHeader>());
        _exampleDataRepo.Setup(r => r.GetExampleDataSetsAsync()).ReturnsAsync(new List<ExampleDataSet>());
        _exampleDataRepo.Setup(r => r.GetTemplatesAsync()).ReturnsAsync(new List<ExampleDataTemplate>());
        _securityRepo.Setup(r => r.GetRolesAsync()).ReturnsAsync(new List<JIM.Models.Security.Role>());

        // Configuration change tracking must be enabled for RebaselineBuiltInConfigurationAsync to do anything.
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "true"
            });

        // The three rate limiting settings, as they would exist post-wipe (rows preserved; not truncated).
        var rateLimitingEnabled = new ServiceSetting
        {
            Key = Constants.SettingKeys.RateLimitingEnabled,
            DisplayName = "API rate limiting enabled",
            Category = ServiceSettingCategory.Security,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true"
        };
        var authenticatedLimit = new ServiceSetting
        {
            Key = Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute,
            DisplayName = "Authenticated API requests per minute",
            Category = ServiceSettingCategory.Security,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = "300"
        };
        var unauthenticatedLimit = new ServiceSetting
        {
            Key = Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute,
            DisplayName = "Unauthenticated API requests per minute",
            Category = ServiceSettingCategory.Security,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = "30"
        };
        _settingsRepo.Setup(r => r.GetAllSettingsAsync())
            .ReturnsAsync(new List<ServiceSetting> { rateLimitingEnabled, authenticatedLimit, unauthenticatedLimit });
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.RateLimitingEnabled)).ReturnsAsync(rateLimitingEnabled);
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute)).ReturnsAsync(authenticatedLimit);
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute)).ReturnsAsync(unauthenticatedLimit);
        _settingsRepo.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings());
        _settingsRepo.Setup(r => r.UpdateServiceSettingsAsync(It.IsAny<ServiceSettings>())).Returns(Task.CompletedTask);

        // The configuration change hash key setting: RecordSeededServiceSettingBaselineAsync's capture path reads
        // (and, on first use, generates and persists) this to compute keyed hashes of secret values in snapshots.
        _protection = new FakeProtection();
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(new byte[32]))
            });

        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task ResetSystemAsync_RestoresCreateActivityAndVersionOneBaselineForRateLimitingSettingsAsync()
    {
        await _jim.System.ResetSystemAsync(
            ActivityInitiatorType.ApiKey, Guid.NewGuid(), "Infrastructure Key", includeAdministrators: false);

        foreach (var key in new[]
                 {
                     Constants.SettingKeys.RateLimitingEnabled,
                     Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute,
                     Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute
                 })
        {
            var activity = _createdActivities.SingleOrDefault(a =>
                a.TargetType == ActivityTargetType.ServiceSetting && a.ServiceSettingKey == key);

            Assert.That(activity, Is.Not.Null, $"a factory reset must re-record the Create Activity for '{key}'");
            Assert.That(activity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
            Assert.That(activity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        }
    }

    [Test]
    public async Task ResetSystemAsync_RateLimitingSettingBaselines_AreGroupedUnderTheSystemInitialisationParentAsync()
    {
        await _jim.System.ResetSystemAsync(
            ActivityInitiatorType.ApiKey, Guid.NewGuid(), "Infrastructure Key", includeAdministrators: false);

        var parentActivity = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(parentActivity, Is.Not.Null, "the reseed must record a System Initialisation parent Activity");

        var rateLimitingActivity = _createdActivities.Single(a =>
            a.TargetType == ActivityTargetType.ServiceSetting && a.ServiceSettingKey == Constants.SettingKeys.RateLimitingEnabled);
        Assert.That(rateLimitingActivity.ParentActivityId, Is.EqualTo(parentActivity!.Id));
    }

    /// <summary>A round-trip credential-protection test double using a recognisable encrypted-value prefix.</summary>
    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
