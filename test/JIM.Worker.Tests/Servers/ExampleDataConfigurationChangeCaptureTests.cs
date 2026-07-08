// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;
using System.Text;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests configuration change-history capture for Example Data Sets and Templates: create/update/delete record a
/// versioned, metadata-only snapshot keyed by <see cref="Activity.ExampleDataSetId"/> /
/// <see cref="Activity.ExampleDataTemplateId"/>, with the shared toggle/dedupe/tombstone behaviours, principal
/// attribution on the (REST-called) Set mutators, and the System-attributed seed baselines. Mirrors
/// <c>ConnectorDefinitionConfigurationChangeCaptureTests</c>.
/// </summary>
[TestFixture]
public class ExampleDataConfigurationChangeCaptureTests
{
    private const int DataSetId = 55;
    private const int TemplateId = 71;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IExampleDataRepository> _exampleRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private Activity? _completedActivity;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _exampleRepo = new Mock<IExampleDataRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ExampleData).Returns(_exampleRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };
        _completedActivity = null;

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- Example Data Set --------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateExampleDataSetAsync_UserInitiated_RecordsCreateActivityWithVersionOneSnapshotAsync()
    {
        var dataSet = BuildDataSet(DataSetId, "Job Titles");
        _exampleRepo.Setup(r => r.CreateExampleDataSetAsync(It.IsAny<ExampleDataSet>())).Returns(Task.CompletedTask);
        _exampleRepo.Setup(r => r.GetExampleDataSetAsync(DataSetId)).ReturnsAsync(dataSet);
        SetupMaxVersion(ActivityTargetType.ExampleDataSet, 0);

        await _jim.ExampleData.CreateExampleDataSetAsync(dataSet, NewUser(), changeReason: "seeded custom set");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.ExampleDataSet));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(_completedActivity!.ExampleDataSetId, Is.EqualTo(DataSetId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ExampleDataSet\""));
        // The audit stamp is applied to the entity.
        Assert.That(dataSet.CreatedByType, Is.EqualTo(ActivityInitiatorType.User));
    }

    [Test]
    public async Task UpdateExampleDataSetAsync_ApiKeyInitiated_AttributesActivityToTheKeyAsync()
    {
        var dataSet = BuildDataSet(DataSetId, "Job Titles");
        _exampleRepo.Setup(r => r.UpdateExampleDataSetAsync(It.IsAny<ExampleDataSet>())).Returns(Task.CompletedTask);
        _exampleRepo.Setup(r => r.GetExampleDataSetAsync(DataSetId)).ReturnsAsync(dataSet);
        SetupMaxVersion(ActivityTargetType.ExampleDataSet, 0);
        var key = new ApiKey { Id = Guid.NewGuid(), Name = "provisioning-key" };

        await _jim.ExampleData.UpdateExampleDataSetAsync(dataSet, key, changeReason: "added values");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(_completedActivity!.InitiatedById, Is.EqualTo(key.Id));
        Assert.That(_completedActivity!.ExampleDataSetId, Is.EqualTo(DataSetId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteExampleDataSetAsync_RecordsTombstoneWithoutVersionAsync()
    {
        var dataSet = BuildDataSet(DataSetId, "Job Titles");
        _exampleRepo.Setup(r => r.GetExampleDataSetAsync(DataSetId)).ReturnsAsync(dataSet);
        _exampleRepo.Setup(r => r.DeleteExampleDataSetAsync(DataSetId)).Returns(Task.CompletedTask);

        await _jim.ExampleData.DeleteExampleDataSetAsync(DataSetId, NewUser(), changeReason: "removing set");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Not.Null, "a delete records a tombstone snapshot");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "a deletion tombstone consumes no version");
        Assert.That(_completedActivity!.ExampleDataSetId, Is.Null, "the tombstone is unlinked; the set is deleted before completion");
    }

    [Test]
    public async Task DeleteExampleDataSetAsync_NotFound_RecordsNoActivityAsync()
    {
        _exampleRepo.Setup(r => r.GetExampleDataSetAsync(DataSetId)).ReturnsAsync((ExampleDataSet?)null);

        await _jim.ExampleData.DeleteExampleDataSetAsync(DataSetId, NewUser());

        Assert.That(_completedActivity, Is.Null, "a not-found delete records no configuration-change activity");
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public async Task UpdateExampleDataSetAsync_WhenTrackingDisabled_RecordsActivityButNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);
        var dataSet = BuildDataSet(DataSetId, "Job Titles");
        _exampleRepo.Setup(r => r.UpdateExampleDataSetAsync(It.IsAny<ExampleDataSet>())).Returns(Task.CompletedTask);

        await _jim.ExampleData.UpdateExampleDataSetAsync(dataSet, NewUser(), changeReason: "no tracking");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null);
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("no tracking"));
    }

    [Test]
    public async Task RecordSeededExampleDataSetBaselineAsync_RecordsSystemCreateChildWithVersionOneBaselineAsync()
    {
        var dataSet = BuildDataSet(DataSetId, "Companies");
        _exampleRepo.Setup(r => r.GetExampleDataSetAsync(DataSetId)).ReturnsAsync(dataSet);
        SetupMaxVersion(ActivityTargetType.ExampleDataSet, 0);
        var parentActivityId = Guid.NewGuid();

        await _jim.ExampleData.RecordSeededExampleDataSetBaselineAsync(DataSetId, "Companies", parentActivityId);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.ParentActivityId, Is.EqualTo(parentActivityId));
        Assert.That(_completedActivity!.ExampleDataSetId, Is.EqualTo(DataSetId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    // -- Example Data Template ---------------------------------------------------------------------------------------

    [Test]
    public async Task CreateTemplateAsync_SystemInitiated_RecordsCreateActivityWithVersionOneSnapshotAsync()
    {
        var template = BuildTemplate(TemplateId, "Users and Groups");
        _exampleRepo.Setup(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>())).Returns(Task.CompletedTask);
        _exampleRepo.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
        SetupMaxVersion(ActivityTargetType.ExampleDataTemplate, 0);

        await _jim.ExampleData.CreateTemplateAsync(template, ActivityInitiatorType.System, null, "System", changeReason: "new template");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.ExampleDataTemplate));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.ExampleDataTemplateId, Is.EqualTo(TemplateId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ExampleDataTemplate\""));
    }

    [Test]
    public async Task RecordSeededExampleDataTemplateBaselineAsync_RecordsSystemCreateChildWithVersionOneBaselineAsync()
    {
        var template = BuildTemplate(TemplateId, "Users and Groups");
        _exampleRepo.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);
        SetupMaxVersion(ActivityTargetType.ExampleDataTemplate, 0);
        var parentActivityId = Guid.NewGuid();

        await _jim.ExampleData.RecordSeededExampleDataTemplateBaselineAsync(TemplateId, "Users and Groups", parentActivityId);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.ExampleDataTemplate));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.ParentActivityId, Is.EqualTo(parentActivityId));
        Assert.That(_completedActivity!.ExampleDataTemplateId, Is.EqualTo(TemplateId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    // -- helpers -----------------------------------------------------------------------------------------------------

    private static readonly byte[] HashKeyBytes = new byte[32];

    private void SetupTrackingSetting(bool enabled) =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(HashKeyBytes))
            });

    private void SetupMaxVersion(ActivityTargetType targetType, int max) =>
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(targetType, It.IsAny<int>()))
            .ReturnsAsync(max);

    private static ExampleDataSet BuildDataSet(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Culture = "en",
        BuiltIn = true,
        Values = { new ExampleDataSetValue { Id = 1, StringValue = "Engineer" } }
    };

    private static ExampleDataTemplate BuildTemplate(int id, string name)
    {
        var template = new ExampleDataTemplate { Id = id, Name = name, BuiltIn = true };
        template.ObjectTypes.Add(new ExampleDataObjectType
        {
            Id = 1,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" },
            ObjectsToCreate = 100
        });
        return template;
    }

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

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
