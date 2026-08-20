// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Exceptions;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;
using System.Text;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests the validation and guard behaviour of the Data Generation Template CRUD surface (#894):
/// create and full update validate the template before recording anything (an invalid request leaves no Activity
/// or snapshot behind), template names are unique, and a scalar-only rename skips full graph validation while
/// still rejecting an empty name. Activity/snapshot behaviour itself is pinned by
/// <c>ExampleDataConfigurationChangeCaptureTests</c>, whose fixture this mirrors.
/// </summary>
[TestFixture]
public class ExampleDataTemplateCrudTests
{
    private const int TemplateId = 71;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IExampleDataRepository> _exampleRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;

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
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting();
        SetupHashKeySetting();
        SetupMaxVersion();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- create ------------------------------------------------------------------------------------------------------

    [Test]
    public void CreateTemplateAsync_InvalidTemplate_ThrowsAndRecordsNoActivity()
    {
        // no Object Types: Validate() must reject this before any Activity is created or anything persists.
        var template = new ExampleDataTemplate { Name = "Empty Template" };

        Assert.That(async () => await _jim.ExampleData.CreateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin"),
            Throws.TypeOf<ExampleDataTemplateException>());

        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never,
            "an invalid create must record no Activity");
        _exampleRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never,
            "an invalid create must persist nothing");
    }

    [Test]
    public void CreateTemplateAsync_DuplicateName_Throws()
    {
        var template = BuildValidTemplate(0, "Users and Groups");
        _exampleRepo.Setup(r => r.GetTemplateAsync("Users and Groups")).ReturnsAsync(BuildValidTemplate(99, "Users and Groups"));

        Assert.That(async () => await _jim.ExampleData.CreateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin"),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("already exists"));

        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never,
            "a duplicate-name create must record no Activity");
        _exampleRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never,
            "a duplicate-name create must persist nothing");
    }

    // -- update ------------------------------------------------------------------------------------------------------

    [Test]
    public void UpdateTemplateAsync_InvalidTemplate_Throws()
    {
        // no Object Types: a full (graph-replacing) update must be validated exactly like a create.
        var template = new ExampleDataTemplate { Id = TemplateId, Name = "Empty Template" };

        Assert.That(async () => await _jim.ExampleData.UpdateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin"),
            Throws.TypeOf<ExampleDataTemplateException>());

        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never,
            "an invalid update must record no Activity");
        _exampleRepo.Verify(r => r.UpdateTemplateAsync(It.IsAny<ExampleDataTemplate>(), It.IsAny<bool>()), Times.Never,
            "an invalid update must persist nothing");
    }

    [Test]
    public void UpdateTemplateAsync_DuplicateName_Throws()
    {
        // renaming template 71 onto a name held by template 99 must be rejected.
        var template = BuildValidTemplate(TemplateId, "Users and Groups");
        _exampleRepo.Setup(r => r.GetTemplateAsync("Users and Groups")).ReturnsAsync(BuildValidTemplate(99, "Users and Groups"));

        Assert.That(async () => await _jim.ExampleData.UpdateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin"),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("already exists"));

        _exampleRepo.Verify(r => r.UpdateTemplateAsync(It.IsAny<ExampleDataTemplate>(), It.IsAny<bool>()), Times.Never,
            "a duplicate-name update must persist nothing");
    }

    [Test]
    public async Task UpdateTemplateAsync_RenameOnly_SkipsGraphValidationButRejectsEmptyNameAsync()
    {
        // a scalar-only rename does not replace the graph, so an incoming template with no Object Types is fine...
        var rename = new ExampleDataTemplate { Id = TemplateId, Name = "Renamed Template" };
        _exampleRepo.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(BuildValidTemplate(TemplateId, "Renamed Template"));

        await _jim.ExampleData.UpdateTemplateAsync(rename, ActivityInitiatorType.User, Guid.NewGuid(), "Admin", replaceObjectTypes: false);
        _exampleRepo.Verify(r => r.UpdateTemplateAsync(rename, false), Times.Once,
            "a rename-only update with no Object Types must not be rejected by graph validation");

        // ...but an empty name is still rejected.
        var unnamed = new ExampleDataTemplate { Id = TemplateId, Name = string.Empty };
        Assert.That(async () => await _jim.ExampleData.UpdateTemplateAsync(unnamed, ActivityInitiatorType.User, Guid.NewGuid(), "Admin", replaceObjectTypes: false),
            Throws.TypeOf<ExampleDataTemplateException>());
    }

    [Test]
    public async Task UpdateTemplateAsync_ReplaceObjectTypesFlag_PassedThroughToRepositoryAsync()
    {
        var template = BuildValidTemplate(TemplateId, "Users and Groups");
        _exampleRepo.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(template);

        await _jim.ExampleData.UpdateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin");
        _exampleRepo.Verify(r => r.UpdateTemplateAsync(template, true), Times.Once,
            "replaceObjectTypes defaults to true and must reach the repository");

        await _jim.ExampleData.UpdateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin", replaceObjectTypes: false);
        _exampleRepo.Verify(r => r.UpdateTemplateAsync(template, false), Times.Once,
            "replaceObjectTypes: false must reach the repository");
    }

    // -- helpers -----------------------------------------------------------------------------------------------------

    private static readonly byte[] HashKeyBytes = new byte[32];

    private void SetupTrackingSetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "true"
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

    private void SetupMaxVersion() =>
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ExampleDataTemplate, It.IsAny<int>()))
            .ReturnsAsync(0);

    private static ExampleDataTemplate BuildValidTemplate(int id, string name)
    {
        var template = new ExampleDataTemplate { Id = id, Name = name };
        template.ObjectTypes.Add(new ExampleDataObjectType
        {
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" },
            ObjectsToCreate = 100
        });
        return template;
    }

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
