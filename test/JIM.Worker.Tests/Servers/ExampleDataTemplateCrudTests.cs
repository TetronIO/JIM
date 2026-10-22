// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
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

    // -- copy (#1466) ------------------------------------------------------------------------------------------------

    [Test]
    public async Task CopyTemplateAsync_BuiltInSource_CreatesAnEditableCopyUnderTheNewNameAsync()
    {
        var source = BuildValidTemplate(TemplateId, "Users and Groups");
        source.BuiltIn = true;
        _exampleRepo.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(source);
        ExampleDataTemplate? persisted = null;
        _exampleRepo.Setup(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()))
            .Callback<ExampleDataTemplate>(t => { persisted = t; t.Id = 99; })
            .Returns(Task.CompletedTask);

        var copy = await _jim.ExampleData.CopyTemplateAsync(TemplateId, "My Users and Groups", ActivityInitiatorType.User, Guid.NewGuid(), "Admin");

        Assert.That(persisted, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy.Id, Is.EqualTo(99));
            Assert.That(persisted!.Name, Is.EqualTo("My Users and Groups"));
            Assert.That(persisted!.BuiltIn, Is.False);
            Assert.That(persisted!.ObjectTypes, Has.Count.EqualTo(1));
            Assert.That(persisted!.ObjectTypes[0], Is.Not.SameAs(source.ObjectTypes[0]));
            Assert.That(persisted!.ObjectTypes[0].ObjectsToCreate, Is.EqualTo(100));
        }

        _activityRepo.Verify(r => r.CreateActivityAsync(It.Is<Activity>(a =>
            a.TargetOperationType == ActivityTargetOperationType.Create && a.TargetName == "My Users and Groups")), Times.Once);
    }

    [Test]
    public void CopyTemplateAsync_SourceMissing_ThrowsAndRecordsNoActivity()
    {
        _exampleRepo.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync((ExampleDataTemplate?)null);

        Assert.That(async () => await _jim.ExampleData.CopyTemplateAsync(TemplateId, "Copy", ActivityInitiatorType.User, Guid.NewGuid(), "Admin"),
            Throws.TypeOf<KeyNotFoundException>());

        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public void CopyTemplateAsync_NameTaken_ThrowsAndPersistsNothing()
    {
        _exampleRepo.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(BuildValidTemplate(TemplateId, "Users and Groups"));
        _exampleRepo.Setup(r => r.GetTemplateAsync("Users and Groups")).ReturnsAsync(BuildValidTemplate(TemplateId, "Users and Groups"));

        Assert.That(async () => await _jim.ExampleData.CopyTemplateAsync(TemplateId, "Users and Groups", ActivityInitiatorType.User, Guid.NewGuid(), "Admin"),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("already exists"));

        _exampleRepo.Verify(r => r.CreateTemplateAsync(It.IsAny<ExampleDataTemplate>()), Times.Never);
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

    // -- expressions (#2055 Phase 3) ---------------------------------------------------------------------------------

    [Test]
    public void CreateTemplateAsync_ExpressionThatDoesNotParse_ThrowsNamingTheAttributeAndRecordsNoActivity()
    {
        var template = BuildTemplateWithExpression(0, "Lower(mv[\"First Name\"]");

        Assert.That(async () => await _jim.ExampleData.CreateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin"),
            Throws.TypeOf<ExampleDataTemplateAttributeException>().With.Message.Contains("Account Name").And.Message.Contains("does not parse"));

        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public void CreateTemplateAsync_ExpressionReadingAConnectedSystemObject_Throws()
    {
        var template = BuildTemplateWithExpression(0, "Lower(cs[\"givenName\"])");

        Assert.That(async () => await _jim.ExampleData.CreateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin"),
            Throws.TypeOf<ExampleDataTemplateAttributeException>().With.Message.Contains("cs[\"givenName\"]"));
    }

    [Test]
    public async Task CreateTemplateAsync_ExpressionReadingAnAttributeTheTemplateDoesNotGenerate_SavesWithAWarningAsync()
    {
        // A template is built an attribute at a time, so an expression may name one not added yet: said, not refused.
        var template = BuildTemplateWithExpression(0, "Lower(mv[\"Nickname\"])");

        await _jim.ExampleData.CreateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin");

        Assert.That(template.SaveWarnings, Has.One.Contains("mv[\"Nickname\"]"));
    }

    [Test]
    public async Task UpdateTemplateAsync_UnchangedExpressionThatDoesNotParse_SavesWithAWarningAsync()
    {
        _exampleRepo.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(BuildTemplateWithExpression(TemplateId, "Lower(mv[\"First Name\"]"));
        var template = BuildTemplateWithExpression(TemplateId, "Lower(mv[\"First Name\"]");
        template.Name = "Renamed";

        await _jim.ExampleData.UpdateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin");

        Assert.That(template.SaveWarnings, Has.One.Contains("does not parse"));
    }

    [Test]
    public void UpdateTemplateAsync_ExpressionChangedToOneThatDoesNotParse_Throws()
    {
        _exampleRepo.Setup(r => r.GetTemplateAsync(TemplateId)).ReturnsAsync(BuildTemplateWithExpression(TemplateId, "Lower(mv[\"First Name\"])"));
        var template = BuildTemplateWithExpression(TemplateId, "Lower(mv[\"First Name\"]");

        Assert.That(async () => await _jim.ExampleData.UpdateTemplateAsync(template, ActivityInitiatorType.User, Guid.NewGuid(), "Admin"),
            Throws.TypeOf<ExampleDataTemplateAttributeException>());
    }

    [Test]
    public void DescribeDanglingReads_AnExpressionReadingAnAttributeTheTemplateNeverGenerates_NamesItOnce()
    {
        // Generation logs these once per run, so a finished template with a dangling read is not silent.
        var messages = ExampleDataServer.DescribeDanglingReads(BuildTemplateWithExpression(1, "Lower(mv[\"Nickname\"]) + mv[\"Nickname\"]"));

        Assert.That(messages, Has.One.Contains("mv[\"Nickname\"]").And.Contain("Account Name"));
    }

    [Test]
    public void DescribeDanglingReads_EveryReadGenerated_IsEmpty()
    {
        Assert.That(ExampleDataServer.DescribeDanglingReads(BuildTemplateWithExpression(1, "Lower(mv[\"First Name\"])")), Is.Empty);
    }

    private static ExampleDataTemplate BuildTemplateWithExpression(int id, string expression)
    {
        var template = new ExampleDataTemplate { Id = id, Name = "People" };
        var objectType = new ExampleDataObjectType
        {
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" },
            ObjectsToCreate = 10
        };
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Id = 11, Name = "First Name", Type = AttributeDataType.Text },
            Pattern = "Ada"
        });
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute
        {
            MetaverseAttribute = new MetaverseAttribute { Id = 12, Name = "Account Name", Type = AttributeDataType.Text },
            Expression = expression
        });
        template.ObjectTypes.Add(objectType);
        return template;
    }

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
